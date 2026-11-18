#!/usr/bin/env python3
"""Hand-builds a minimal flattened device tree (DTB) for riscv-emu's own
"virt-like" machine - no dtc available on this host, and the tree needed is
small enough that building the binary format directly is simpler than
installing one. Describes memory, one CPU with its ISA string and timebase,
/chosen with bootargs plus the initrd location, a PLIC, a ns16550a UART
node for console=ttyS0 (interrupt-driven, routed through the PLIC - see
Phase 5's "finished" section), and a virtio-mmio node for the Phase 6
virtio-blk device (Phase 7).

console=hvc0 was tried first (no memory-mapped device needed, just SBI
calls) but real OpenSBI + real qemu-system-riscv64 booting this exact
kernel image with console=hvc0 also ends in "Warning: unable to open an
initial console." (see boot/real_qemu_smp1_hvc0.log) - this kernel build
simply has no SBI-hvc console driver compiled in, so hvc0 was never going
to work here regardless of this emulator's own correctness. The same
kernel with console=ttyS0 instead boots all the way to a shell prompt under
real QEMU (boot/real_qemu_smp1.log) - hence the UART node now, and
boot.sh's cmdline switching to match.
"""
import struct
import sys

FDT_BEGIN_NODE = 0x1
FDT_END_NODE = 0x2
FDT_PROP = 0x3
FDT_NOP = 0x4
FDT_END = 0x9


class DtbBuilder:
    def __init__(self):
        self.struct = bytearray()
        self.strings = bytearray()
        self.string_offsets = {}

    def _str_off(self, name: str) -> int:
        if name not in self.string_offsets:
            self.string_offsets[name] = len(self.strings)
            self.strings += name.encode() + b"\0"
        return self.string_offsets[name]

    def begin_node(self, name: str):
        self.struct += struct.pack(">I", FDT_BEGIN_NODE)
        data = name.encode() + b"\0"
        self.struct += data
        while len(self.struct) % 4:
            self.struct += b"\0"

    def end_node(self):
        self.struct += struct.pack(">I", FDT_END_NODE)

    def prop_raw(self, name: str, data: bytes):
        self.struct += struct.pack(">I", FDT_PROP)
        self.struct += struct.pack(">II", len(data), self._str_off(name))
        self.struct += data
        while len(self.struct) % 4:
            self.struct += b"\0"

    def prop_u32(self, name: str, *values):
        self.prop_raw(name, b"".join(struct.pack(">I", v) for v in values))

    def prop_u64(self, name: str, *values):
        self.prop_raw(name, b"".join(struct.pack(">Q", v) for v in values))

    def prop_str(self, name: str, value: str):
        self.prop_raw(name, value.encode() + b"\0")

    def prop_empty(self, name: str):
        self.prop_raw(name, b"")

    def finish(self) -> bytes:
        self.struct += struct.pack(">I", FDT_END)

        off_mem_rsvmap = 40  # header is 10 big-endian u32 = 40 bytes
        rsvmap = struct.pack(">QQ", 0, 0)  # one empty terminator entry

        off_dt_struct = off_mem_rsvmap + len(rsvmap)
        off_dt_strings = off_dt_struct + len(self.struct)
        totalsize = off_dt_strings + len(self.strings)

        header = struct.pack(
            ">IIIIIIIIII",
            0xD00DFEED,       # magic
            totalsize,
            off_dt_struct,
            off_dt_strings,
            off_mem_rsvmap,
            17,               # version
            16,               # last_comp_version
            0,                # boot_cpuid_phys
            len(self.strings),
            len(self.struct),
        )
        return header + rsvmap + bytes(self.struct) + bytes(self.strings)


def build(mem_base: int, mem_size: int, initrd_start: int, initrd_end: int,
          bootargs: str) -> bytes:
    d = DtbBuilder()
    d.begin_node("")
    d.prop_u32("#address-cells", 2)
    d.prop_u32("#size-cells", 2)
    d.prop_str("compatible", "riscv-emu,virt")
    d.prop_str("model", "riscv-emu,virt")

    d.begin_node("chosen")
    d.prop_str("bootargs", bootargs)
    d.prop_u64("linux,initrd-start", initrd_start)
    d.prop_u64("linux,initrd-end", initrd_end)
    # Linux's early_init_dt_scan_chosen() feeds this straight into the
    # entropy pool via add_bootloader_randomness() - the standard way a
    # bootloader with no hardware RNG hands Linux a seed so random_init()
    # doesn't have to wait for one. This machine has no hardware RNG, no
    # RTC, and (deliberately, for make compare's reproducibility - see
    # Pit/CLINT's own remarks in cpu.c/devices.c) no real timing jitter
    # for Linux's own CPU-jitter fallback to harvest either - confirmed
    # directly: the Phase 5 boot stall was a tight `csrrs _, time, zero`
    # loop, exactly that fallback spinning forever waiting for entropy
    # that was never coming. Fixed (not hidden): not real entropy, a
    # hardcoded constant - fine for what this seeds (unblocking boot),
    # wrong for anything that actually needs secrecy, and kept fixed
    # rather than regenerated every run so a boot attempt stays
    # reproducible for debugging, the same reason mtime ticks by
    # instruction count instead of wall-clock time.
    d.prop_raw("rng-seed", bytes.fromhex(
        "d1b1b53c6b6f6a0e8e9f9a2e3c4d5e6f"
        "7081920a1b2c3d4e5f60718293a4b5c6"
        "d7e8f90a1b2c3d4e5f60718293a4b5c6"
        "d7e8f900112233445566778899aabbcc"
    ))
    d.end_node()

    d.begin_node(f"memory@{mem_base:x}")
    d.prop_str("device_type", "memory")
    d.prop_u64("reg", mem_base, mem_size)
    d.end_node()

    d.begin_node("cpus")
    d.prop_u32("#address-cells", 1)
    d.prop_u32("#size-cells", 0)
    # Matches real qemu-system-riscv64 -M virt's own CLINT (10 MHz), not our
    # mtime's true behavior (it advances once per retired instruction, not
    # once per real 100ns tick). An "honest" low value like 1 Hz was tried
    # first and broke the kernel's clocksource math outright: the mult/shift
    # conversion from raw ticks to nanoseconds is derived from this exact
    # field assuming a realistic (kHz-GHz range) frequency, and a 1 Hz input
    # produces a degenerate conversion that turns ordinary sub-second delays
    # (udelay/mdelay, RCU stall timeouts, the "wait for next scheduler tick"
    # loop at the end of start_kernel) into waits for hundreds of millions of
    # ticks - observed directly as a busy loop stuck for 500M+ instructions
    # at a `rdtime`-based wait. Claiming the same value real hardware does
    # keeps that math sane; it costs only going slightly faster or slower
    # than real wall-clock time for any given delay, which nothing here
    # depends on being accurate.
    d.prop_u32("timebase-frequency", 10000000)

    d.begin_node("cpu@0")
    d.prop_str("device_type", "cpu")
    d.prop_u32("reg", 0)
    d.prop_str("status", "okay")
    d.prop_str("compatible", "riscv")
    # Canonical extension order (I,M,A,F,D,...,C per the ISA naming spec).
    # f/d were added once cpu.c implemented them (see README's "Phase 5,
    # resolved") - without them here, riscv_fill_hwcap() never sets
    # elf_hwcap's F/D bits, and without those the kernel's illegal-
    # instruction handler has no reason to treat an FP opcode as a lazy-
    # FPU-enable candidate rather than a real SIGILL.
    d.prop_str("riscv,isa", "rv64imafdc_sstc")
    d.prop_str("mmu-type", "riscv,sv39")

    d.begin_node("interrupt-controller")
    d.prop_u32("#interrupt-cells", 1)
    d.prop_empty("interrupt-controller")
    d.prop_str("compatible", "riscv,cpu-intc")
    d.prop_u32("phandle", 1)
    d.end_node()

    d.end_node()  # cpu@0
    d.end_node()  # cpus

    # Without this node, Linux's CLINT timer driver (drivers/clocksource/
    # timer-clint.c) never binds to anything - device drivers in Linux are
    # matched by devicetree "compatible" strings, not by assuming hardware
    # exists just because the emulator implements it. Its total absence
    # here left jiffies/the scheduler tick permanently frozen: no node, no
    # driver probe, no timer interrupt ever armed - which looked, from the
    # guest's side, exactly like a generic spinlock that never gets
    # released, since whatever was supposed to release it was itself
    # waiting on a scheduler tick that could never arrive.
    d.begin_node("clint@2000000")
    d.prop_raw("compatible", b"sifive,clint0\0riscv,clint0\0")
    d.prop_u64("reg", 0x2000000, 0x10000)
    d.prop_raw("interrupts-extended", struct.pack(">IIII", 1, 3, 1, 7))  # cpu0 intc: SSIP(3), MTIP(7)
    d.end_node()

    # PLIC: routes the UART's receive-ready line to the hart's S-mode
    # external interrupt (SEIP) - see PLIC_BASE's own remarks in emu.h for
    # why console *output* never needed this but console *input* does.
    # interrupts-extended pairs this hart's local interrupt-controller
    # (phandle 1, declared on cpu@0 above) with context 0 = M-mode external
    # (11) and context 1 = S-mode external (9), QEMU virt's own convention
    # for context numbering per hart - Linux's PLIC driver only ever
    # registers against the S-mode entry, same as real hardware.
    d.begin_node("plic@c000000")
    d.prop_raw("compatible", b"sifive,plic-1.0.0\0riscv,plic0\0")
    d.prop_u32("#interrupt-cells", 1)
    d.prop_empty("interrupt-controller")
    d.prop_u32("riscv,ndev", 31)
    d.prop_u64("reg", 0xc000000, 0x400000)
    d.prop_raw("interrupts-extended", struct.pack(">IIII", 1, 11, 1, 9))
    d.prop_u32("phandle", 2)
    d.end_node()

    # ns16550a at UART_BASE (src/emu.h) - src/devices.c's uart_read/uart_write
    # already implement the THRE/DR bits a polling 8250 console driver reads;
    # this node is what makes Linux's of-based 8250 driver actually bind to
    # it and register ttyS0 in the first place. clock-frequency matches the
    # standard 16550 reference clock real qemu-system-riscv64 -M virt uses
    # for the same compatible string. interrupt-parent/interrupts (added
    # alongside the PLIC node above) wire UART_IRQ through it - without
    # this, console *output* still works (it never needed interrupts), but
    # the 8250 driver's receive path has nothing to ever wake it up.
    d.begin_node("serial@10000000")
    d.prop_str("compatible", "ns16550a")
    d.prop_u64("reg", 0x10000000, 0x100)
    d.prop_u32("clock-frequency", 3686400)
    d.prop_u32("interrupt-parent", 2)
    d.prop_u32("interrupts", 10)
    d.end_node()

    # TESTDEV (src/emu.h's TESTDEV_BASE/testdev_write) as the same
    # "sifive,test0"/syscon device real qemu-system-riscv64 -M virt exposes
    # at this address (confirmed via `-machine dumpdtb=...` against real
    # QEMU with `-bios default`, the same way every other node's exact
    # compatible/reg values in this file were) - with the same two child
    # nodes real QEMU's own DTB has, so the kernel's generic `syscon-
    # poweroff`/`syscon-reboot` drivers (drivers/power/reset/) bind to it
    # and register pm_power_off/the restart handler themselves, entirely
    # independent of SBI. This turned out to be *the* mechanism real
    # QEMU's "poweroff"/"reboot" guest commands actually use - not SBI
    # SRST, which this project's own firmware also now implements (see
    # firmware.S's own comment) but which a real guest kernel never ends
    # up calling once this syscon path is available, confirmed by a
    # temporary UART trace in firmware.S's SBI SRST handler never firing
    # while this node was still missing, on a kernel that cleanly powers
    # off/exits under real QEMU every time. value/offset/regmap match
    # FINISHER_PASS/FINISHER_RESET and this node's own phandle exactly.
    d.begin_node("test@100000")
    d.prop_u32("phandle", 3)
    d.prop_u64("reg", 0x100000, 0x1000)
    d.prop_raw("compatible", b"sifive,test1\0sifive,test0\0syscon\0")
    d.end_node()

    d.begin_node("poweroff")
    d.prop_raw("compatible", b"syscon-poweroff\0")
    d.prop_u32("regmap", 3)
    d.prop_u32("offset", 0)
    d.prop_u32("value", 0x5555)
    d.end_node()

    d.begin_node("reboot")
    d.prop_raw("compatible", b"syscon-reboot\0")
    d.prop_u32("regmap", 3)
    d.prop_u32("offset", 0)
    d.prop_u32("value", 0x7777)
    d.end_node()

    # google,goldfish-rtc at RTC_BASE (src/emu.h) - real wall-clock time, not
    # simulated/deterministic mtime, so Linux actually sets its own system
    # clock on boot instead of staying at whatever default it starts with.
    # Address and compatible string match real qemu-system-riscv64 -M virt
    # exactly (confirmed via `-machine dumpdtb=...`, the same way every
    # other node's exact values in this file were) - added specifically
    # because its absence left an Alpine guest's TLS certificate validation
    # rejecting every certificate as "not yet valid".
    d.begin_node("rtc@101000")
    d.prop_u64("reg", 0x101000, 0x1000)
    d.prop_u32("interrupt-parent", 2)
    d.prop_u32("interrupts", 11)
    d.prop_raw("compatible", b"google,goldfish-rtc\0")
    d.end_node()

    # virtio-mmio at VIRTIO_BASE (src/emu.h) - Phase 6 built and verified the
    # device itself (virtio-blk over the "modern" v2 transport) only against
    # its own bare-metal test program; this node is what makes Linux's
    # virtio_mmio driver actually probe it. compatible/reg/interrupts match
    # real qemu-system-riscv64 -M virt exactly (confirmed via
    # `-M virt,dumpdtb=...` with one `-device virtio-blk-device` attached,
    # same way VIRTIO_BASE/VIRTIO_IRQ themselves were confirmed in Phase 6 -
    # not guessed from the spec). Real QEMU's virt machine exposes 8 of
    # these slots (0x10001000-0x10008000); this emulator only implements
    # the one device Phase 6 needed, at the highest-addressed slot, which
    # is also the first one QEMU itself fills for a single plugged-in
    # device - see Phase 6's own remarks on why "highest first" surprised
    # this project once already.
    d.begin_node("virtio_mmio@10008000")
    d.prop_str("compatible", "virtio,mmio")
    d.prop_u64("reg", 0x10008000, 0x1000)
    d.prop_u32("interrupt-parent", 2)
    d.prop_u32("interrupts", 8)
    d.end_node()

    # virtio-net (Phase 9) - the second virtio-mmio slot, confirmed the same
    # empirical way the first one's own address/IRQ were (see VIRTIO_NET_BASE's
    # own remarks in src/emu.h): `cat /proc/iomem` from a real QEMU boot with
    # both -device virtio-blk-device and -device virtio-net-device attached.
    d.begin_node("virtio_mmio@10007000")
    d.prop_str("compatible", "virtio,mmio")
    d.prop_u64("reg", 0x10007000, 0x1000)
    d.prop_u32("interrupt-parent", 2)
    d.prop_u32("interrupts", 7)
    d.end_node()

    d.end_node()  # root
    return d.finish()


if __name__ == "__main__":
    out_path, mem_base, mem_size, initrd_start, initrd_end, bootargs = sys.argv[1:7]
    blob = build(int(mem_base, 0), int(mem_size, 0), int(initrd_start, 0),
                 int(initrd_end, 0), bootargs)
    with open(out_path, "wb") as f:
        f.write(blob)
    print(f"wrote {out_path}: {len(blob)} bytes")

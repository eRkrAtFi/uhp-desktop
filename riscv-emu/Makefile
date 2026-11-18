# riscv-emu - build with the portable GCC in ../tools/w64devkit for the C
# interpreter, and with LLVM (clang/lld/llvm-objcopy) for RISC-V guest test
# programs, since no GNU RISC-V cross toolchain is installed on this host.
CC       ?= gcc
CFLAGS   ?= -std=c99 -O2 -Wall -Wextra -Wpedantic
# Resolved through PATH, not a baked-in absolute path: a direct /c/...
# path inside a recipe resolves inconsistently depending on what spawned
# this make (w64devkit's sh translates it one way when make itself was
# launched from an MSYS/git-bash parent, and differently when launched
# fresh from PowerShell/cmd) - same reason emu86's Makefile resolves gcc
# and nasm through PATH rather than a literal path. Add LLVM's and QEMU's
# directories to PATH before invoking make.
CLANG    ?= clang
OBJCOPY  ?= llvm-objcopy
QEMU     ?= qemu-system-riscv64

# rv64im, no C (compressed) extension: Phase 1's decoder only understands
# full 32-bit encodings. -mno-relax keeps codegen deterministic across
# rebuilds; it has no effect on correctness either way.
GUEST_FLAGS := --target=riscv64-unknown-elf -march=rv64im -mabi=lp64 -mno-relax \
               -nostdlib -static -Wl,-T,tests/link.ld

SRC := src/cpu.c src/devices.c src/main.c src/net.c src/dtb.c
HDR := src/emu.h
BIN := riscv-emu.exe
# virtio-net's user-mode NAT backend (src/net.c): Winsock for the actual
# sockets, IP Helper for ICMP (IcmpSendEcho2 - real raw ICMP needs admin
# on Windows, which this project cannot assume the user has; the IP
# Helper API is the same mechanism ping.exe itself uses and needs no
# elevation).
NET_LIBS := -lws2_32 -liphlpapi
# winmm: timeBeginPeriod, so WFI's real-time sleep (src/cpu.c) actually
# gets the ~1ms granularity it asks for instead of Windows' default
# ~15.6ms system timer tick - see realtime_clock_init's own comment.
NET_LIBS += -lwinmm

all: $(BIN)

$(BIN): $(SRC) $(HDR)
	$(CC) $(CFLAGS) -o $@ $(SRC) $(NET_LIBS)

# Produces both the ELF (what QEMU's -kernel loads, respecting its own
# header for the entry address) and a flat binary at the same link address
# (what our own loader expects, since it does not parse ELF yet).
tests/%.elf: tests/%.S tests/link.ld
	$(CLANG) $(GUEST_FLAGS) -fuse-ld=lld -o $@ $<

tests/%.bin: tests/%.elf
	$(OBJCOPY) -O binary $< $@

# boot/firmware.bin/.elf are a real guest program too (Linux's M-mode
# firmware stand-in - see boot/firmware.S), built the same way as
# tests/*.S but against boot/firmware.ld's link address instead of
# tests/link.ld's. Without this rule, editing firmware.S silently kept
# running the previous binary with no error at all - confirmed the hard
# way, costing a real debugging session chasing a "stuck in M-mode" freeze
# that was actually just a stale firmware.bin (see README's "Phase 5,
# finished" section).
boot/firmware.elf: boot/firmware.S boot/firmware.ld
	$(CLANG) --target=riscv64-unknown-elf -march=rv64im -mabi=lp64 -mno-relax \
	         -nostdlib -static -Wl,-T,boot/firmware.ld -fuse-ld=lld -o $@ $<

boot/firmware.bin: boot/firmware.elf
	$(OBJCOPY) -O binary $< $@

firmware: boot/firmware.bin

# Phase 6's virtio-blk differential test: unlike every other test here, it
# is C, not assembly - a virtqueue's descriptor/avail/used ring layout is
# tedious and error-prone to build by hand in asm, so tests/virtio_start.S
# is just enough to set sp and call into tests/virtio_test.c. -mcmodel=medany
# is required (not just stylistic) for the C file specifically: the default
# model generates gp-relative addressing for .bss globals that lld cannot
# resolve without a real gp-initializing crt0, which this freestanding
# environment deliberately has none of - confirmed by hitting exactly that
# link error first.
VIRTIO_FLAGS := --target=riscv64-unknown-elf -march=rv64im -mabi=lp64 -mcmodel=medany -mno-relax \
                -nostdlib -static -Wl,-T,tests/link.ld

tests/virtio_test.elf: tests/virtio_start.S tests/virtio_test.c tests/link.ld
	$(CLANG) $(VIRTIO_FLAGS) -fuse-ld=lld -o $@ tests/virtio_start.S tests/virtio_test.c

tests/virtio_disk.img:
	python3 -c "open('$@', 'wb').write(bytes(1024*1024))"

virtio-test: $(BIN) tests/virtio_test.bin tests/virtio_disk.img
	./$(BIN) --deterministic-time --disk tests/virtio_disk.img tests/virtio_test.bin

# Same idea as compare, but there are now two things that must match: the
# console output (as before) and the disk image itself (virtio-blk's writes
# actually need to land in the same place under both implementations, not
# just report the same status byte back to the guest). Each side gets its
# own copy of the pristine disk image so a run never contaminates the
# other's starting state or a rerun's.
virtio-compare: $(BIN) tests/virtio_test.elf tests/virtio_test.bin tests/virtio_disk.img
	@cp tests/virtio_disk.img build-virtio-riscv-emu.img
	@./$(BIN) --deterministic-time --disk build-virtio-riscv-emu.img tests/virtio_test.bin > build-virtio-riscv-emu.out
	@cp tests/virtio_disk.img build-virtio-qemu.img
	@-$(QEMU) -M virt -bios none -kernel tests/virtio_test.elf -nographic -no-reboot \
		-drive file=build-virtio-qemu.img,format=raw,id=hd0,if=none \
		-device virtio-blk-device,drive=hd0 \
		-global virtio-mmio.force-legacy=false > build-virtio-qemu.out 2>/dev/null
	@diff -u build-virtio-riscv-emu.out build-virtio-qemu.out
	@cmp build-virtio-riscv-emu.img build-virtio-qemu.img
	@echo "riscv-emu matches QEMU (console output and disk contents both)"

test: $(BIN) tests/selftest.bin
	./$(BIN) --deterministic-time --dump tests/selftest.bin

# The real check: the same guest program must produce the same bytes under
# riscv-emu and under real qemu-system-riscv64. riscv-emu prints UART
# output directly; QEMU's virt machine routes its UART to stdio under
# -nographic. The guest ends both runs itself via the test-finisher MMIO
# device, so no timeout or process-kill is needed on either side.
compare: $(BIN) tests/selftest.elf tests/selftest.bin
	@./$(BIN) --deterministic-time tests/selftest.bin > build-riscv-emu.out
	@-$(QEMU) -M virt -bios none -kernel tests/selftest.elf -nographic -no-reboot > build-qemu.out 2>/dev/null
	@diff -u build-riscv-emu.out build-qemu.out && echo "riscv-emu matches QEMU"

trace: $(BIN) tests/selftest.bin
	./$(BIN) --trace --max 500 tests/selftest.bin 2>&1 | head -80

# --- Integration tests: boot/firmware.S's actual SBI dispatch, not the
# CPU's trap/ECALL mechanics in isolation (selftest.S already covers those
# without ever loading firmware.S at all). tests/integration_sbi.S hands off
# to S-mode the normal Phase 5 way (firmware at RAM_BASE, this payload at
# RAM_BASE+0x200000, matching boot.sh's own real-kernel address) and probes
# the base/HSM/legacy SBI calls a real kernel actually relies on.
tests/integration_sbi.elf: tests/integration_sbi.S tests/integration.ld
	$(CLANG) --target=riscv64-unknown-elf -march=rv64im -mabi=lp64 -mno-relax \
	         -nostdlib -static -Wl,-T,tests/integration.ld -fuse-ld=lld -o $@ $<

integration: $(BIN) tests/integration_sbi.bin boot/firmware.bin
	./$(BIN) --deterministic-time --entry 0x80000000 --load 0x80000000 boot/firmware.bin \
	         --load 0x80200000 tests/integration_sbi.bin \
	         --reg a0 0 --reg a1 0 --reg t3 0x80200000 --max 1000000

# Differential half of the same test: real QEMU + real OpenSBI (-bios
# default) against the identical guest payload. OpenSBI's own boot banner
# is expected noise on that side (nothing here tries to suppress or match
# it) - only the payload's own last line of output ("OK", or "FAIL n") is
# what's actually being compared, so this checks the tail of each side's
# output rather than a full diff the way compare/virtio-compare do.
#
# tr -d '\r' on both sides before the diff: real QEMU's own OpenSBI
# console driver terminates its lines CRLF (ordinary real-UART-console
# behavior), tests/integration_sbi.S's own payload writes plain "OK\n" -
# a trailing \r is exactly the same kind of each-side's-own-firmware
# noise the comment above already tolerates everywhere else, just
# reaching the payload's own final line too. Confirmed NOT an emulator
# bug (riscv-emu's own output has no \r at all, matching the payload's
# own literal bytes) and confirmed environment-dependent rather than
# this comparison's own content ever actually differing: identical bytes
# on disk compared equal when make's own recipe shell ran the diff
# locally, yet compared unequal both under a plain interactive bash
# invocation of the exact same command and in CI (GitHub's windows-latest
# runner) - two different shells evidently handle process substitution's
# pipe text/binary mode differently here, which this sidesteps rather
# than depends on.
integration-compare: $(BIN) tests/integration_sbi.elf tests/integration_sbi.bin boot/firmware.bin
	@./$(BIN) --deterministic-time --entry 0x80000000 --load 0x80000000 boot/firmware.bin \
	          --load 0x80200000 tests/integration_sbi.bin \
	          --reg a0 0 --reg a1 0 --reg t3 0x80200000 --max 1000000 > build-integration-riscv-emu.out
	@-$(QEMU) -M virt -bios default -kernel tests/integration_sbi.elf -nographic -no-reboot \
		> build-integration-qemu.out 2>/dev/null
	@tail -n 1 build-integration-riscv-emu.out
	@tail -n 1 build-integration-qemu.out
	@diff <(tail -n 1 build-integration-riscv-emu.out | tr -d '\r') <(tail -n 1 build-integration-qemu.out | tr -d '\r') \
		&& echo "riscv-emu matches QEMU (SBI dispatch, both via their own firmware)"

# --- Boot and interactive tests: boot/boot_test.sh and
# boot/interactive_test.sh are self-contained shell scripts (not Makefile
# recipes) since both need real wall-clock polling loops a Makefile recipe
# would be an awkward place to express. See their own comments for what
# each one actually checks. interactive_test.sh needs bash specifically
# (it uses a coproc to deliver input - see its own comment for why a plain
# fifo does not work on this host).
boot-test: $(BIN)
	sh boot/boot_test.sh

interactive-test: $(BIN)
	bash boot/interactive_test.sh

# Phase 7: mkfs.ext2 + mount + write + md5sum + umount, under both
# riscv-emu and real qemu-system-riscv64, diffing the two interactive
# transcripts - see boot/fs_compare.sh's own top comment for exactly what
# is and is not compared and why. Boots two full kernels, each with its
# own scripted delays, so this is on the same order of wall-clock time as
# boot-test/interactive-test and deliberately not part of test-all either.
fs-compare: $(BIN)
	bash boot/fs_compare.sh

# Everything that does not need multiple minutes of real wall-clock time
# for a full Linux boot - the fast, "does the last change still work"
# suite. boot-test/interactive-test/fs-compare are deliberately not part
# of this.
test-all: compare virtio-compare integration-compare
	@echo "unit + differential + integration: all passed"

clean:
	rm -f $(BIN) tests/*.elf tests/*.bin build-*.out build-*.img tests/virtio_disk.img

.PHONY: all test compare trace clean firmware virtio-test virtio-compare \
        integration integration-compare boot-test interactive-test \
        fs-compare test-all

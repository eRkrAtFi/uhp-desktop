/* riscv-emu - a RV64 system emulator, built in phases toward booting real
 * Linux, with QEMU's own qemu-system-riscv64 as the reference at every step.
 *
 * Phase map (see README.md for the reasoning):
 *   1. RV64IM core, M-mode only, flat physical memory, two MMIO devices
 *      (UART, test-finisher) - enough to run and verify bare-metal test
 *      programs. Done.
 *   2. CSRs, traps/exceptions, privilege levels (M/S/U), and PMP. <- this
 *      file's scope today. PMP is here, not deferred, because without it
 *      S/U-mode cannot fetch or access anything at all on real hardware
 *      (the default with zero active PMP regions is "deny" for S/U) - it
 *      is not an optional extra on top of privilege levels, it is required
 *      to observe S/U mode doing anything whatsoever. Timer interrupts
 *      need a timer device (CLINT, Phase 4), so they are not part of this
 *      phase - the trap *plumbing* they would use (mie/mip/mstatus bits,
 *      delegation) is, so Phase 4 only has to add the device.
 *   3. Sv39 paging (the mode riscv64 Linux uses). Done.
 *   4. CLINT (the M-mode-only timer/software-interrupt device) and real
 *      interrupt delivery. <- this file's scope today. SBI itself is not
 *      new emulator code: like `emu86`'s BIOS, the "firmware" that services
 *      ECALL-from-S is guest-executed M-mode code our test writes, not a
 *      host intercept - we already have full M-mode CPU execution, so there
 *      is nothing to intercept. PLIC (external/device interrupts, as
 *      opposed to the timer/software ones CLINT raises) is deferred: a
 *      minimal headless Linux boot's console can poll the UART directly
 *      without it, so it is revisited only if Phase 5 actually needs it.
 *   5. Boot an actual riscv64 Linux kernel + initramfs.
 *
 * Layout mirrors emu86: cpu.c is the interpreter, devices.c is host-served
 * MMIO (emu86's bios.c equivalent), main.c is machine setup.
 */
#ifndef RISCV_EMU_H
#define RISCV_EMU_H

#include <stdint.h>
#include <stdio.h>

/* Privilege levels, using the same 2-bit encoding the spec stores in
 * mstatus.MPP: 0 = U, 1 = S, 3 = M (2 is reserved for H-mode, unimplemented). */
enum { PRIV_U = 0, PRIV_S = 1, PRIV_M = 3 };

/* Phase 5 discovered, empirically, that no realistic prebuilt kernel is
 * actually just RV64IM: OpenSBI reports the real hardware this was all
 * tested against as "rv64imafdch" - every mainstream toolchain defaults to
 * -march=rv64gc, which uses compressed encodings for nearly everything and
 * atomics throughout kernel locking. A and C are therefore implemented as
 * part of Phase 5, not deferred - without them, a stock kernel Image
 * cannot execute its own first few instructions. F/D (floating point) was
 * also not implemented at first, on a bet that Linux's per-process lazy
 * FPU enable (only on that process's first FP instruction) would mean a
 * boot to a plain shell never actually executes one - that bet lost: a
 * normal toolchain targeting rv64gc freely emits fld/fsd for ordinary
 * 8-byte memory moves regardless of whether the C source does real
 * floating-point math, so /init hit one almost immediately. F/D is now
 * implemented - see README.md's "Phase 5, resolved" section. */

#define MEM_SIZE   (256u * 1024 * 1024)   /* 256 MiB, like our QEMU -m default */
#define RAM_BASE   0x80000000u            /* matches qemu-system-riscv64 -M virt */

/* MMIO device base addresses, matching QEMU's "virt" machine memory map so
 * the same guest code and the same host tooling work against either. */
#define UART_BASE       0x10000000u   /* 16550-compatible; we model THR/LSR only */
#define UART_SIZE       0x100u
#define TESTDEV_BASE    0x00100000u   /* SiFive "syscon" test finisher */
#define TESTDEV_SIZE    0x1000u
#define RTC_BASE        0x00101000u   /* google,goldfish-rtc - real QEMU's own -M virt address */
#define RTC_SIZE        0x1000u
#define CLINT_BASE      0x02000000u   /* SiFive CLINT: msip, mtimecmp, mtime */
#define CLINT_SIZE      0x10000u
#define CLINT_MSIP_OFF      0x0000u   /* one 4-byte word per hart; we have one hart */
#define CLINT_MTIMECMP_OFF  0x4000u   /* one 8-byte register per hart */
#define CLINT_MTIME_OFF     0xBFF8u   /* one 8-byte free-running counter, shared */

/* SiFive/RISC-V PLIC: only what interactive console input actually needs.
 * One source (the UART), two contexts (QEMU virt's own convention: context
 * 2*hartid = this hart's M-mode view, 2*hartid+1 = its S-mode view - with
 * one hart, that is simply contexts 0 and 1). Phase 4 deferred this
 * entirely ("a headless boot's console can poll the UART directly without
 * it") - console *output* still can; Linux's 8250 driver's *receive* path
 * cannot, confirmed directly: typing at a real `~ #` shell prompt produced
 * no response at all with no PLIC and UART_BASE's devicetree node carrying
 * no interrupts property, for ~190M further instructions of real wall-clock
 * time to rule out "just needed to wait longer" - nothing ever polled the
 * byte that had genuinely already arrived in our UART model's receive side. */
#define PLIC_BASE        0x0C000000u
#define PLIC_SIZE        0x400000u
#define PLIC_MAX_IRQ     32            /* irq 0 is reserved/unused, same as real PLIC */
#define PLIC_NUM_CONTEXTS 2            /* 0 = M-mode, 1 = S-mode (this hart only) */
#define UART_IRQ         10            /* arbitrary but fixed; nothing needs it to match real QEMU's own numbering */

/* VirtIO-MMIO (version 2 / "modern", not legacy) - deliberately placed at
 * the exact address and IRQ QEMU's own riscv "virt" machine uses for its
 * first virtio-mmio slot, so the same disk image and guest-facing protocol
 * can be differentially verified against a real virtio-blk-device on
 * qemu-system-riscv64 -M virt (see README's Phase 6). One device, one
 * virtqueue (virtio-blk needs only a single "requestq") - no indirect
 * descriptors, no EVENT_IDX, no multi-device virtio-mmio chain; just
 * enough for Linux's virtio_blk + virtio_mmio drivers to work with a real
 * disk image. */
/* 0x10008000, not the lower-addressed 0x10001000 one might expect: QEMU's
 * riscv virt machine creates 8 virtio-mmio slots (0x10001000-0x10008000)
 * but fills them highest-address-first as devices are plugged in, so a
 * single `-device virtio-blk-device` lands on 0x10008000 - confirmed
 * empirically (tests/virtio_test.c's own slot-scan loop), not assumed.
 * IRQ 1 is that same slot's own fixed IRQ in QEMU's layout. */
#define VIRTIO_BASE      0x10008000u
#define VIRTIO_SIZE      0x1000u
#define VIRTIO_IRQ       8              /* this slot's own fixed IRQ in QEMU's layout, confirmed via dumpdtb */
#define VIRTQ_MAX_SIZE   256u          /* advertised via QueueNumMax; the driver may ask for less */

/* Phase 9: virtio-net, the *second* virtio-mmio slot - confirmed the same
 * empirical way VIRTIO_BASE/VIRTIO_IRQ above were (this time via
 * `cat /proc/iomem` from a real-QEMU boot with both `-device
 * virtio-blk-device` and `-device virtio-net-device` attached, in that
 * order on the command line): QEMU's virt machine still fills slots
 * highest-address-first as each device is created, so the first device
 * (blk) keeps 0x10008000 and the second (net) takes the next slot down,
 * 0x10007000, with that slot's own fixed IRQ (7). See src/net.c for the
 * user-mode NAT backend this talks to; see devices.c's virtio_net_read/
 * write for the register-level half, which otherwise mirrors virtio-blk's
 * own virtio_read/write above almost exactly. */
#define VIRTIO_NET_BASE  0x10007000u
#define VIRTIO_NET_SIZE  0x1000u
#define VIRTIO_NET_IRQ   7

typedef struct {
    uint64_t desc_addr;     /* QueueDescLow/High: the descriptor table */
    uint64_t driver_addr;   /* QueueDriverLow/High: the avail ring ("driver area" in v2 naming) */
    uint64_t device_addr;   /* QueueDeviceLow/High: the used ring ("device area" in v2 naming) */
    uint32_t num;           /* QueueNum: the driver's chosen queue size (<= VIRTQ_MAX_SIZE) */
    uint32_t ready;         /* QueueReady */
    uint16_t last_avail_idx; /* how far into the avail ring we've already consumed */
} VirtQueue;

/* Values the test finisher recognizes when written as a 32-bit word. */
#define FINISHER_PASS       0x5555u
#define FINISHER_FAIL_CODE(code) (0x3333u | ((uint32_t)(code) << 16))
#define FINISHER_RESET      0x7777u

typedef struct {
    uint64_t x[32];   /* x0 is wired to zero; we still store it for uniformity */
    /* F/D register file: all 32 are 64-bit regardless of mstatus.FS or the
     * value's own width - a 32-bit (single-precision) value is stored
     * NaN-boxed (upper 32 bits all 1) per spec, which f_gets()/f_sets() in
     * cpu.c enforce on every access; f0 has no hardwired-zero special case
     * the way x0 does. fcsr packs frm (bits 7:5) and fflags (bits 4:0), the
     * same layout the real CSR uses, so csr_read/write for 0x001/0x002/0x003
     * are just masked views of this one field. */
    uint64_t f[32];
    uint64_t fcsr;
    uint64_t pc;
    uint64_t insns;
    int halted;
    int exit_code;

    int priv;         /* current privilege level: PRIV_U/PRIV_S/PRIV_M */

    /* Machine-level CSRs. mstatus holds both the machine-view and (via a
     * read/write mask) the supervisor-view sstatus - real hardware keeps
     * one register underneath both names, and modelling it as two would
     * invite the two copies to disagree. Same idea for mie/mip vs sie/sip. */
    uint64_t mstatus;
    uint64_t medeleg;
    uint64_t mideleg;
    uint64_t mie;
    uint64_t mip;
    uint64_t mtvec;
    uint64_t mscratch;
    uint64_t mepc;
    uint64_t mcause;
    uint64_t mtval;

    /* Supervisor-level CSRs with no machine-level counterpart. */
    uint64_t stvec;
    uint64_t sscratch;
    uint64_t sepc;
    uint64_t scause;
    uint64_t stval;
    uint64_t satp;     /* stored but inert until Phase 3 implements paging */

    /* Physical Memory Protection: without at least one active region, real
     * hardware (and QEMU) denies every S/U-mode memory access and
     * instruction fetch by default - M-mode firmware always configures PMP
     * before it ever drops into S-mode. 16 entries matches real RV64 (and
     * QEMU's virt machine); pmpcfg packs 8 one-byte configs per 64-bit CSR,
     * but we keep one byte of config per entry instead of two packed
     * 64-bit words, since that is what every check and every CSR access
     * actually wants - csr_read/write pack and unpack it on the two
     * occasions (pmpcfg0, pmpcfg2) anything touches the packed form. */
    uint8_t pmpcfg[16];
    uint64_t pmpaddr[16];

    /* CLINT state. mtime advances once per retired instruction - a
     * deliberate choice, not an approximation of wall-clock time: it keeps
     * `make compare` reproducible, since it does not have to match QEMU's
     * own (real-time-driven) mtime tick-for-tick, only agree on the
     * observable outcome (an armed timer eventually fires). Tests achieve
     * that by arming mtimecmp to an already-past value (0), which is
     * "expired" under any correct clock immediately, sidestepping any need
     * for the two clocks to move in lockstep. */
    uint64_t mtime;
    uint64_t mtimecmp;
    uint32_t msip;

    /* Sstc: a second, S-mode-usable timer comparator (CSR 0x14D) that
     * raises STIP directly from mtime >= stimecmp, no M-mode round trip
     * (SBI call or CLINT MMIO) involved at all. Real hardware this project
     * was tested against advertises Sstc, and a stock kernel checks for it
     * before trying anything else - without it (and without a devicetree
     * CLINT node, and without the kernel ever calling the legacy SBI timer
     * call either) jiffies simply never advances, which surfaced as a
     * generic kernel spinlock that looked like it was waiting forever: see
     * README.md's Phase 5 section. */
    uint64_t stimecmp;

    /* UART IER (16550 offset 1, DLAB=0): the only UART register besides
     * THR/RBR/LSR this model tracks the value of, and only for one bit -
     * see plic_irq_live's own comment on why. Real hardware's DLAB=1 use of
     * this same offset (the baud-rate divisor's low byte, during the one-
     * time 8250 init sequence) gets captured here too since nothing tracks
     * LCR/DLAB at all, but that is harmless: the real IER write Linux's
     * 8250 driver always makes right after clearing DLAB overwrites it
     * before anything reads it as IER. */
    uint8_t uart_ier;

    /* PLIC state - see PLIC_BASE above. plic_claimed tracks, per irq, "a
     * context has claimed this and not yet written complete" - without it,
     * a still-physically-pending level source (the UART, as long as its
     * receive side has an unread byte) would immediately re-assert the
     * hart's external-interrupt line the instant the handler returns, even
     * mid-service, which live-locks instead of letting the handler actually
     * drain the UART. Real PLIC hardware suppresses re-delivery of a claimed
     * source the same way until its matching complete write. */
    uint32_t plic_priority[PLIC_MAX_IRQ];
    uint32_t plic_enable[PLIC_NUM_CONTEXTS];
    uint32_t plic_threshold[PLIC_NUM_CONTEXTS];
    uint32_t plic_claimed;

    /* VirtIO-MMIO register state - see VIRTIO_BASE above. The disk file
     * itself lives on Machine, not here (a host resource, not guest
     * architectural state - same reasoning as Machine's mem/stdin_ring),
     * but the device's own registers are guest-visible state exactly like
     * PLIC's above, so they follow the same convention. */
    uint32_t virtio_status;
    uint32_t virtio_dev_features_sel;
    uint32_t virtio_drv_features_sel;
    uint32_t virtio_queue_sel;
    uint32_t virtio_isr;
    VirtQueue virtio_q[1];  /* virtio-blk needs exactly one queue */

    /* virtio-net's own register bank, at VIRTIO_NET_BASE - a second,
     * independent virtio-mmio slot, not a second device sharing
     * virtio_q[]/virtio_isr above (real hardware has one register bank
     * per slot; conflating the two would make the two devices stomp on
     * each other's queue/feature-negotiation state). Two virtqueues
     * (net_q[0] = receiveq, net_q[1] = transmitq, the order virtio-net's
     * own spec fixes) - no control queue, since this device offers
     * nothing (link status changes, multiqueue, RSS...) that would need
     * one. */
    uint32_t net_status;
    uint32_t net_dev_features_sel;
    uint32_t net_drv_features_sel;
    uint32_t net_queue_sel;
    uint32_t net_isr;
    VirtQueue net_q[2];

    /* A extension: a single-hart LR/SC reservation. Any store (including
     * another AMO) from this hart invalidates it; a real multi-hart
     * implementation must also invalidate on another hart's write to the
     * same line, which does not apply here - there is only ever one hart. */
    int reservation_valid;
    uint64_t reservation_addr;

    /* A direct-mapped TLB. Discovered necessary, not planned: Sv39's
     * 3-level walk on every single fetch/load/store (no caching at all)
     * turned out to make an actual Linux boot attempt impractically slow -
     * correct, but too slow to ever observe the result. Caches the
     * permission bits already checked (R/W/X, folding in SUM/MXR as they
     * stood at translation time) so a hit skips both the walk and the
     * permission re-check; a satp write or SFENCE.VMA flushes the whole
     * array rather than tracking which entries a change could affect -
     * simpler, and neither is a hot path. */
    struct {
        int valid;
        uint64_t vpn;
        uint64_t ppn;
        int priv;
        int r, w, x;
    } tlb[256];
    uint64_t tlb_hits, tlb_misses; /* temporary Phase 5 diagnostic counters */
    uint64_t rtc_latched_ns; /* goldfish-rtc: TIME_LOW's own last-latched value, for TIME_HIGH to read back */
} CPU;

/* Host stdin, fed to the guest UART's receive side (see uart_read/write
 * and console_getchar/console_input_ready below). A background thread
 * (started in main.c) does the actual blocking read of the real stdin -
 * which might be a real console, a redirected file, or a GUI's piped
 * stdin (QemuManager's ConsoleWindow) - and pushes bytes in here; the
 * interpreter loop only ever pops from it, never blocks on I/O itself.
 * A single-producer/single-consumer ring buffer needs no lock: the reader
 * thread only ever advances write_pos, the interpreter only ever advances
 * read_pos, and the size (a power of two) makes wraparound just a mask. */
#define STDIN_RING_SIZE 256
typedef struct {
    uint8_t buf[STDIN_RING_SIZE];
    /* Plain unsigned int, not volatile - every access goes through
     * __atomic_load_n/__atomic_store_n with explicit acquire/release
     * ordering (see main.c's stdin_reader_thread/console_input_ready/
     * console_getchar), which is what actually guarantees the producer
     * thread's write to buf[] is visible to the interpreter before it
     * ever observes the updated write_pos, and symmetrically that the
     * interpreter's read of buf[] is visible to the producer before it
     * reuses that slot. `volatile` alone (the previous declaration here)
     * only stops a single thread's own compiler from caching/reordering
     * its own accesses to the variable - it says nothing about
     * cross-thread memory visibility or ordering, which is the actual
     * correctness requirement for a producer/consumer ring buffer and
     * was never actually guaranteed before. GCC/Clang built-ins, not
     * <stdatomic.h>'s _Atomic/atomic_* - this project builds as C99 (see
     * Makefile's CFLAGS), which predates <stdatomic.h>; the built-ins
     * provide the identical operations and are available regardless of
     * -std=. */
    unsigned int write_pos;
    unsigned int read_pos;
} StdinRing;

typedef struct {
    CPU cpu;
    uint8_t *mem;      /* guest physical RAM, indexed from 0 (RAM_BASE subtracted) */
    uint64_t mem_size; /* actual guest RAM size - defaults to MEM_SIZE, overridable
                         * via --mem; every RAM bounds check (in_ram, load_at, the
                         * image-too-large check) uses this, not the macro directly,
                         * so a smaller/larger --mem actually takes effect everywhere
                         * rather than just at the initial calloc. */
    int trace;
    StdinRing stdin_ring;
    /* virtio-blk's backing store: a plain raw disk image, opened via
     * --disk <path>. A host resource, not guest architectural state - same
     * reasoning as mem/stdin_ring above - so it lives here, not on CPU
     * alongside the device's own registers. NULL means no disk is
     * attached, which virtio_read reports as DeviceID 0 ("nothing in this
     * slot"), the same convention real QEMU uses for an empty virtio-mmio
     * slot. */
    FILE *disk_file;
    uint64_t disk_size;

    /* virtio-net's user-mode NAT backend state (sockets, the TCP
     * connection table, pending-DNS tracking, ...) - entirely a host
     * resource, same reasoning as disk_file above, and entirely private
     * to src/net.c (defined there, not here, so this header never needs
     * <winsock2.h>). NULL means no net device is attached, exactly like
     * disk_file's NULL meaning no disk - virtio_net_read then reports
     * DeviceID 0, same convention. */
    void *net;

    /* Real time vs. deterministic time (see cpu.c's realtime_mtime/
     * wfi_sleep_realtime and main.c's --deterministic-time) - host
     * resources/config, not guest architectural state, same reasoning as
     * every other field in this struct (as opposed to CPU's own mtime/
     * mtimecmp/stimecmp, which the guest can read and write directly).
     *
     * deterministic_time: 0 (the default) means mtime tracks real host
     * time, scaled to the 10MHz this machine's DTB advertises - a guest
     * sleep()/ping interval/date now actually takes that many real
     * seconds, matching real hardware and real QEMU. 1 means the original
     * Phase 4 behavior, mtime advancing exactly once per retired
     * instruction: deliberately kept, not replaced, for every differential
     * test that compares riscv-emu's output byte-for-byte or instruction-
     * count-for-instruction-count against real QEMU - make test/compare/
     * virtio-compare/integration-compare and boot-test/fs-compare's own
     * invocations of boot.sh all pass --deterministic-time for exactly
     * this reason (see Makefile/boot_test.sh/fs_compare.sh). Without it,
     * two runs of the *same* deterministic test could still disagree with
     * each other depending on incidental host scheduling jitter, which a
     * real regression test cannot tolerate - QEMU's own --icount mode
     * exists for the identical reason, deterministic time this emulator
     * never had an equivalent lever for until now.
     *
     * time_origin_qpc/qpc_frequency: a QueryPerformanceCounter() reading
     * taken right before the main loop starts (see main.c) - "guest
     * mtime == 0" happens then, not at process start, so time spent
     * parsing arguments/loading a multi-hundred-MB kernel+initramfs never
     * counts against the guest's own perceived uptime - and
     * QueryPerformanceFrequency(), needed to convert elapsed host ticks
     * into 10MHz guest ticks regardless of what this host's own counter
     * frequency actually is. Unused when deterministic_time is set. */
    int deterministic_time;
    uint64_t time_origin_qpc;
    uint64_t qpc_frequency;
} Machine;

/* --- real/deterministic time (src/cpu.c) ----------------------------------
 * realtime_clock_init captures time_origin_qpc/qpc_frequency - call once,
 * right before the main instruction loop starts, only when
 * deterministic_time is NOT set (main.c's own call site skips it
 * otherwise, since nothing would ever read those fields in that mode). */
void realtime_clock_init(Machine *m);

/* --- console input (main.c starts the reader thread; devices.c's UART
 * model is the only caller of these two) ---------------------------------- */
void console_input_start(Machine *m);
int  console_input_ready(Machine *m);   /* 1 if a byte is waiting, without consuming it */
int  console_getchar(Machine *m);       /* pops and returns a byte 0-255, or -1 if none waiting */

/* --- memory: guest-physical address space, RAM plus two MMIO windows ---- */
uint8_t  phys_read8(Machine *m, uint64_t addr);
uint16_t phys_read16(Machine *m, uint64_t addr);
uint32_t phys_read32(Machine *m, uint64_t addr);
uint64_t phys_read64(Machine *m, uint64_t addr);
void phys_write8(Machine *m, uint64_t addr, uint8_t val);
void phys_write16(Machine *m, uint64_t addr, uint16_t val);
void phys_write32(Machine *m, uint64_t addr, uint32_t val);
void phys_write64(Machine *m, uint64_t addr, uint64_t val);

/* --- cpu ----------------------------------------------------------------- */
void cpu_reset(Machine *m);
/* Executes one instruction. Returns non-zero once the machine should stop. */
int cpu_step(Machine *m);
void cpu_dump(const CPU *c, FILE *out);
/* Translates va the same way a load would, through whatever satp/paging
 * state the CPU currently has - for --peek, so a virtual address can be
 * inspected even after the guest has relocated itself in physical RAM (the
 * raw Image file on disk no longer matches what is actually at that VA once
 * that has happened). Returns 0 and leaves *pa unset on a page fault. */
int cpu_translate_for_debug(Machine *m, uint64_t va, uint64_t *pa);

/* --- devices (devices.c) -------------------------------------------------- */
int device_covers(uint64_t addr, uint64_t base, uint64_t size);
uint64_t uart_read(Machine *m, uint64_t addr, int width);
void uart_write(Machine *m, uint64_t addr, int width, uint64_t val);
void testdev_write(Machine *m, uint64_t addr, int width, uint64_t val);
uint64_t rtc_read(Machine *m, uint64_t addr, int width);
uint64_t clint_read(Machine *m, uint64_t addr, int width);
void clint_write(Machine *m, uint64_t addr, int width, uint64_t val);
uint64_t plic_read(Machine *m, uint64_t addr, int width);
void plic_write(Machine *m, uint64_t addr, int width, uint64_t val);
/* Non-consuming "what would claim return for this context right now" check -
 * shared between plic_read's own claim/complete handling and cpu.c's
 * check_interrupt, which uses it to drive mip's SEIP bit the same way it
 * already derives MTIP from CLINT state: computed fresh every step, not
 * something software writes directly. Returns 0 (no real IRQ is ever id 0)
 * if nothing pending+enabled+unclaimed clears this context's threshold. */
uint32_t plic_claim_peek(Machine *m, int ctx);
uint64_t virtio_read(Machine *m, uint64_t addr, int width);
void virtio_write(Machine *m, uint64_t addr, int width, uint64_t val);
uint64_t virtio_net_read(Machine *m, uint64_t addr, int width);
void virtio_net_write(Machine *m, uint64_t addr, int width, uint64_t val);
/* The used-ring push every virtqueue-backed device needs (virtio-blk's
 * own request completion, virtio-net's frame delivery in both
 * directions) - one definition, not duplicated, per struct virtq_used's
 * own layout (see this function's definition in devices.c for the actual
 * field offsets). */
void virtq_push_used(Machine *m, VirtQueue *q, uint32_t desc_id, uint32_t len);

/* --- virtio-net's user-mode NAT backend (src/net.c) -----------------------
 * net_init opens the backend (sockets, etc.) and sets m->net; called once
 * from main.c if --net was given, never if it wasn't (m->net stays NULL,
 * exactly like disk_file's NULL meaning "no disk"). net_poll is called
 * periodically from the main interpreter loop (not once per instruction -
 * see main.c's own call site for why) to service pending host sockets
 * (TCP connects/data, DNS replies) and inject anything now ready into the
 * guest's receive virtqueue; virtio_net_write calls into net.c directly
 * for an immediate transmit (see devices.c), since that has to happen
 * synchronously with the guest's own QueueNotify, not on net_poll's own
 * schedule. */
void net_init(Machine *m);
void net_poll(Machine *m);
/* The guest-visible NIC's own MAC address (what eth0 reports inside the
 * guest) - one definition, in net.c, instead of a constant duplicated in
 * both net.c (which builds frames addressed to/from it) and devices.c's
 * virtio_net_read (which reports it through config space): exactly the
 * kind of duplicated-address mistake VIRTIO_BASE's own history in this
 * project (see README's Phase 6) already cost real debugging time once. */
extern const uint8_t net_guest_mac[6];
/* Called by virtio_net_write on a transmitq (index 1) QueueNotify - parses
 * and acts on every newly-available frame on that queue immediately
 * (ARP/DHCP/ICMP/UDP/TCP dispatch all happen here; see net.c). */
void net_transmit(Machine *m);

/* --- devicetree (src/dtb.c) -----------------------------------------------
 * Builds the complete DTB this machine's guest Linux needs, node for node
 * the same tree boot/make_dtb.py builds (see that file's own comments for
 * why each node/value is what it is) - ported into C so --kernel can
 * generate it internally instead of requiring an external Python step.
 * Returns a malloc'd buffer the caller must free(); *out_len is its size. */
uint8_t *build_dtb(uint64_t mem_base, uint64_t mem_size,
                    int has_initrd, uint64_t initrd_start, uint64_t initrd_end,
                    const char *bootargs, uint64_t *out_len);

/* --- GDB remote stub (src/gdbstub.c) - vision/VISION.md roadmap item 3,
 * M3. Takes over the whole instruction-stepping loop for the lifetime of
 * the session - see its own top comment. Only ever called from main.c
 * when --gdb was given; the ordinary (non-gdb) loop in main.c is
 * entirely separate code, so a normal run is unaffected by this file
 * existing. */
void gdbstub_run(Machine *m, int port, int wait_for_debugger, uint64_t max_insns);

#endif /* RISCV_EMU_H */

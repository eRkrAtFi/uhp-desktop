# A minimal M-mode firmware stub: what OpenSBI is on real hardware, reduced
# to exactly what this emulator's own Phase 4 already proved works. It runs
# first, at RAM_BASE, then hands off to a pre-loaded Linux kernel Image the
# same way real "fw_jump" firmware does - the kernel is placed at its own
# text_offset by whatever built this boot's command line (see boot.sh), not
# by this file.
#
# Scope, deliberately: legacy SBI (console putchar/getchar, set timer,
# shutdown) plus the SBI base extension (every modern kernel probes it
# before touching anything else) and the same MTI-to-STI interrupt
# forwarding Phase 4's test already verified against real QEMU. No PLIC,
# no virtio, no HSM for other harts - there is only one hart, and nothing
# this project has built yet needs an external interrupt.
.option norvc

.equ UART_BASE,      0x10000000
.equ TESTDEV,        0x00100000
.equ CLINT_MTIMECMP, 0x02004000

.equ FINISHER_PASS,  0x5555

.section .text
.global _start
_start:
    la sp, fw_stack_top

    la t0, mtrap_handler
    csrw mtvec, t0

    # One NAPOT PMP region covering the whole address space, fully
    # permissive - without it S-mode (the kernel, momentarily) cannot
    # fetch or touch anything at all; see Phase 2's README for why.
    li t0, -1
    csrw pmpaddr0, t0
    li t0, 0x1F                     # R=1 W=1 X=1 A=NAPOT(3) L=0
    csrw pmpcfg0, t0

    # Disarm the timer before ever enabling MTIE - its reset value is
    # implementation-defined, and an already-expired mtimecmp combined
    # with MTIE set would fire immediately, before mtvec is even ready
    # (it still would be here, but the ordering is correct regardless).
    li t0, -1
    li t1, CLINT_MTIMECMP
    sd t0, 0(t1)
    li t0, 1 << 7
    csrs mie, t0

    # Delegate every synchronous exception the kernel's own S-mode handler
    # is meant to field - page faults, illegal instructions, breakpoints,
    # misaligned/access faults, ECALL-from-U - to S-mode. ECALL-from-S
    # (bit 9) is deliberately excluded: SBI calls must always reach this
    # firmware, never the kernel's own trap handler. Phase 2's test only
    # ever delegated one specific cause for its own demonstration; real
    # firmware delegates nearly everything, and a real kernel genuinely
    # needs to field its own page faults rather than have them vanish into
    # an M-mode handler with no idea what to do about one.
    li t0, 0xB1FF
    csrw medeleg, t0

    # Delegate the supervisor timer interrupt (bit 5) unconditionally, not
    # just as a side effect of handling one legacy machine timer interrupt
    # (as Phase 4's design originally had it): with Sstc, STIP is asserted
    # directly from mtime >= stimecmp with no M-mode involvement at all, so
    # there is no MTI trap here to piggyback the delegation onto - without
    # this, the very first STI a Sstc-aware kernel causes would arrive at
    # M-mode instead, which has no handler for it (see mtrap_unexpected).
    #
    # Also delegate the supervisor external interrupt (bit 9): Phase 5's
    # PLIC drives this bit the same hardware-level way CLINT drives MTIP/
    # STIP (see check_interrupt in cpu.c) for the UART's receive side -
    # without delegating it, the very first keystroke at a real shell
    # prompt would arrive here instead of at Linux's PLIC driver, which
    # this firmware (same as the MTI/STI case above) has no handler for.
    li t0, (1 << 5) | (1 << 9)
    csrw mideleg, t0

    # Hand off to the kernel in S-mode. a0 (hartid) and a1 (dtb address)
    # are the two registers the RISC-V Linux boot protocol itself defines;
    # t3 is this firmware's own convention, not Linux's, for the one extra
    # fact only our own loader (not a real bootloader) needs to hand this
    # firmware: where the kernel Image actually starts, since that depends
    # on text_offset from the Image's own header (see boot.sh) and this
    # firmware has no way to read that header itself at this stage. All
    # three are set via --reg before riscv-emu ever starts executing.
    csrw mepc, t3
    li t1, 1 << 11                   # mstatus.MPP = 01 (S)
    csrs mstatus, t1
    mret

# --- M-mode trap vector: SBI calls (ECALL from S) and the machine timer
# interrupt, converted into a supervisor one - identical in spirit to
# Phase 4's test handler, generalized into real SBI semantics. ------------
mtrap_handler:
    addi sp, sp, -128
    sd ra, 0(sp)
    sd t0, 8(sp)
    sd t1, 16(sp)
    sd t2, 24(sp)
    sd t3, 32(sp)
    # a0/a1 are deliberately NOT saved here: every path below (every SBI
    # call, legacy or base, and the one non-SBI interrupt path which never
    # touches them) means to return through them - restoring the caller's
    # original a0/a1 on the way out, as every other register here does,
    # would silently discard that return value every single time. Found
    # the hard way: a real echo-loop test program calling the legacy
    # getchar call saw its result overwritten back to whatever a0 held
    # *before* the ecall (a0's hand-off value, hartid - always 0, always
    # "not negative"), not the actual byte or -1 sbi_legacy_getchar had
    # just computed.
    sd a2, 56(sp)
    sd a6, 64(sp)
    sd a7, 72(sp)

    csrr t0, mcause
    li t1, 0x8000000000000007        # machine timer interrupt
    beq t0, t1, timer_interrupt
    li t1, 9                         # ECALL from S-mode: an SBI call
    beq t0, t1, sbi_call
    j mtrap_unexpected

timer_interrupt:
    li t1, -1
    li t2, CLINT_MTIMECMP
    sd t1, 0(t2)
    li t1, 1 << 5
    csrs mip, t1                     # inject a pending supervisor timer IRQ
    csrs mideleg, t1                 # and let it reach S directly from now on
    j mtrap_return_interrupt

sbi_call:
    # a7 = extension ID, a6 = function ID (modern calls); legacy calls
    # (EID 0-8) take their argument directly in a0, result in a0/a1.
    li t1, 0x10
    beq a7, t1, sbi_base
    li t1, 0x48534D                  # "HSM" - Hart State Management
    beq a7, t1, sbi_hsm
    li t1, 0x4442434E                # "DBCN" - Debug Console
    beq a7, t1, sbi_dbcn
    li t1, 0x53525354                # "SRST" - System Reset
    beq a7, t1, sbi_srst
    li t1, 0
    beq a7, t1, sbi_legacy_set_timer
    li t1, 1
    beq a7, t1, sbi_legacy_putchar
    li t1, 2
    beq a7, t1, sbi_legacy_getchar
    li t1, 8
    beq a7, t1, sbi_legacy_shutdown
    li t1, 3
    beq a7, t1, sbi_legacy_noop      # clear_ipi
    li t1, 4
    beq a7, t1, sbi_legacy_noop      # send_ipi: one hart, nothing to send
    li t1, 5
    beq a7, t1, sbi_legacy_noop      # remote_fence_i
    li t1, 6
    beq a7, t1, sbi_legacy_noop      # remote_sfence_vma
    li t1, 7
    beq a7, t1, sbi_legacy_noop      # remote_sfence_vma_asid
    # Unimplemented modern extension: report "not supported" (-2) in a0,
    # matching the SBI calling convention's own error code, rather than a
    # host diagnostic - the kernel is meant to handle this gracefully by
    # not using whatever it was probing for.
    li a0, -2
    li a1, 0
    j mtrap_return_exception

sbi_legacy_set_timer:
    li t2, CLINT_MTIMECMP
    sd a0, 0(t2)
    li t1, 1 << 5
    csrc mip, t1                     # only M-mode can clear an injected STIP
    li a0, 0
    j mtrap_return_exception
sbi_legacy_putchar:
    li t2, UART_BASE
    sb a0, 0(t2)
    li a0, 0
    j mtrap_return_exception
sbi_legacy_getchar:
    # Real polling-UART convention: check LSR's data-ready bit (0) before
    # ever reading RBR - unlike THR, RBR has no well-defined "nothing here"
    # value of its own, so the emptiness check has to happen first, not be
    # inferred from what comes back.
    li t2, UART_BASE
    lb t1, 5(t2)
    andi t1, t1, 1
    beqz t1, 1f
    lb a0, 0(t2)
    j mtrap_return_exception
1:  li a0, -1                        # legacy getchar: -1 means "no input"
    j mtrap_return_exception
sbi_legacy_shutdown:
    li t1, FINISHER_PASS
    li t2, TESTDEV
    sw t1, 0(t2)
    j mtrap_unexpected                # the finisher halts the machine; if it
                                       # somehow returns, there is nowhere
                                       # sensible left to go

# SBI SRST extension (EID "SRST" = 0x53525354). A modern kernel's own
# poweroff/reboot syscall path (what Alpine's `poweroff`/`reboot` commands
# ultimately reach) goes through this, not the legacy EID-8 shutdown call
# above - needed specifically so a guest with a *real*, persistent root
# filesystem on virtio-blk (not just this project's own throwaway test
# images) can cleanly end a run instead of leaving the disk file exactly
# as a hard kill would, which a later run would see as an uncleanly
# unmounted filesystem. FID 0 (system_reset) is the only function this
# extension defines; a0/a1 carry the reset type/reason, both ignored -
# "poweroff" and "reboot" both just end this emulator's own process the
# same way the legacy shutdown call already does (consistent with this
# firmware having nowhere to actually *reboot into* - there is no bootrom
# to re-enter), rather than attempting a true in-place restart.
sbi_srst:
    li t1, FINISHER_PASS
    li t2, TESTDEV
    sw t1, 0(t2)
    j mtrap_unexpected
sbi_legacy_noop:
    li a0, 0
    j mtrap_return_exception

# SBI DBCN extension (EID "DBCN" = 0x4442434E) - a modern kernel's console
# driver prefers this over the legacy putchar/getchar calls above when it
# probes and finds it present. Without it, the kernel finds no usable
# console at all (probe_extension on legacy still succeeds, but recent
# hvc-over-SBI drivers require DBCN specifically, not merely "some" SBI
# console) and boots silently from this firmware's own point of view -
# this was the actual reason Phase 5's boot showed no kernel log output
# whatsoever, console=hvc0/earlycon=sbi notwithstanding; the tight loop
# investigated before this was being debugged blind, by instruction trace
# and register dumps alone, for exactly that reason.
#
# Only t0-t3 and a2 are touched below (all already saved/restored by
# mtrap_handler) - t4-t6 are deliberately never used here: this firmware
# has nothing else that needs them live across a trap either, which is
# why they were never part of that save list to begin with.
sbi_dbcn:
    li t1, 0                         # console_write(num_bytes, addr_lo, addr_hi)
    beq a6, t1, sbi_dbcn_write
    li t1, 1                         # console_read(num_bytes, addr_lo, addr_hi)
    beq a6, t1, sbi_dbcn_read
    li t1, 2                         # console_write_byte(byte)
    beq a6, t1, sbi_dbcn_write_byte
    li a0, -2
    li a1, 0
    j mtrap_return_exception

sbi_dbcn_write:
    # addr_hi (a2) is ignored - this machine never has more than 4 GiB of
    # RAM for a caller to need it, and the SBI spec requires a *physical*
    # address here regardless, which is also the only kind of address
    # this never-paged firmware ever deals in.
    mv t0, a0                        # original count, returned in a1
    mv t1, a0                        # remaining count
    mv t2, a1                        # source cursor
    beqz t1, dbcn_write_done
dbcn_write_loop:
    lb t3, 0(t2)
    li a2, UART_BASE
    sb t3, 0(a2)
    addi t2, t2, 1
    addi t1, t1, -1
    bnez t1, dbcn_write_loop
dbcn_write_done:
    li a0, 0                         # SBI_SUCCESS
    mv a1, t0
    j mtrap_return_exception

sbi_dbcn_write_byte:
    li t0, UART_BASE
    sb a0, 0(t0)
    li a0, 0
    li a1, 0
    j mtrap_return_exception

sbi_dbcn_read:
    # Stops at the first byte that isn't available yet rather than
    # blocking, same "poll LSR first" convention as the legacy getchar
    # call above - a console_read is allowed to return fewer bytes than
    # asked for.
    mv t0, a0                         # remaining
    mv t1, a1                         # dest cursor
    li t2, 0                          # bytes actually read
    beqz t0, dbcn_read_done
dbcn_read_loop:
    li a2, UART_BASE
    lb t3, 5(a2)                      # LSR
    andi t3, t3, 1
    beqz t3, dbcn_read_done
    li a2, UART_BASE
    lb t3, 0(a2)                      # RBR
    sb t3, 0(t1)
    addi t1, t1, 1
    addi t2, t2, 1
    addi t0, t0, -1
    bnez t0, dbcn_read_loop
dbcn_read_done:
    li a0, 0
    mv a1, t2
    j mtrap_return_exception

# SBI HSM extension (EID "HSM" = 0x48534D). Boot was stalling on this: with
# the extension unprobe-able (and this firmware answering every call on it
# with "not supported"), the kernel's own per-hart bring-up code never got
# the "this hart is already started" answer it was polling for and spun
# forever - see riscv-emu/README.md's Phase 5 section. Genuinely only one
# hart exists here, so the whole extension reduces to "hart 0 is always
# already running; nothing else exists."
sbi_hsm:
    li t1, 0                         # hart_start(hartid, start_addr, opaque)
    beq a6, t1, sbi_hsm_start
    li t1, 1                         # hart_stop()
    beq a6, t1, sbi_hsm_stop
    li t1, 2                         # hart_get_status(hartid)
    beq a6, t1, sbi_hsm_get_status
    li a0, -2                        # suspend etc: not supported
    j mtrap_return_exception
sbi_hsm_start:
    beqz a0, sbi_hsm_already_available  # a0 = hartid; 0 is this hart, already running
    li a0, -3                        # SBI_ERR_INVALID_PARAM: no such hart
    j mtrap_return_exception
sbi_hsm_already_available:
    li a0, -6                        # SBI_ERR_ALREADY_AVAILABLE
    j mtrap_return_exception
sbi_hsm_stop:
    # The only hart there is stopping itself: nothing could ever start it
    # again (hart_start above never actually starts anything), so this is
    # as final as the legacy shutdown call - halt the same way, rather than
    # return and leave the CPU running code that assumes it is now stopped.
    li t1, FINISHER_PASS
    li t2, TESTDEV
    sw t1, 0(t2)
    j mtrap_unexpected
sbi_hsm_get_status:
    beqz a0, sbi_hsm_started
    li a0, -3                        # SBI_ERR_INVALID_PARAM: no such hart
    j mtrap_return_exception
sbi_hsm_started:
    li a0, 0                         # SBI_SUCCESS
    li a1, 0                         # HSM_STATE_STARTED
    j mtrap_return_exception

sbi_base:
    # SBI base extension (EID 0x10): every modern kernel probes this
    # before touching anything else. a6 selects the function.
    li t1, 0                         # get_spec_version
    beq a6, t1, sbi_base_spec_version
    li t1, 1                         # get_impl_id
    beq a6, t1, sbi_base_zero
    li t1, 2                         # get_impl_version
    beq a6, t1, sbi_base_zero
    li t1, 3                         # probe_extension
    beq a6, t1, sbi_base_probe
    li t1, 4                         # get_mvendorid
    beq a6, t1, sbi_base_zero
    li t1, 5                         # get_marchid
    beq a6, t1, sbi_base_zero
    li t1, 6                         # get_mimpid
    beq a6, t1, sbi_base_zero
    li a0, -2
    li a1, 0
    j mtrap_return_exception
sbi_base_spec_version:
    li a1, 2                         # SBI v0.2: major=0 (bits[31:24]), minor=2
    li a0, 0
    j mtrap_return_exception
sbi_base_zero:
    li a1, 0
    li a0, 0
    j mtrap_return_exception
sbi_base_probe:
    # a0 holds the extension ID being probed; answer 1 (present) for
    # legacy (0x00-0x08), base (0x10), HSM (0x48534D), DBCN
    # (0x4442434E), and SRST (0x53525354) - the only ones implemented -
    # 0 for anything else.
    li t1, 8
    bgt a0, t1, probe_check_base
    li a1, 1
    li a0, 0
    j mtrap_return_exception
probe_check_base:
    li t1, 0x10
    beq a0, t1, probe_yes
    li t1, 0x4442434E
    beq a0, t1, probe_yes
    li t1, 0x48534D
    beq a0, t1, probe_yes
    li t1, 0x53525354
    beq a0, t1, probe_yes
    li a1, 0
    li a0, 0
    j mtrap_return_exception
probe_yes:
    li a1, 1
    li a0, 0
    j mtrap_return_exception

mtrap_return_exception:
    csrr t0, mepc
    addi t0, t0, 4
    csrw mepc, t0
mtrap_return_interrupt:
    ld ra, 0(sp)
    ld t0, 8(sp)
    ld t1, 16(sp)
    ld t2, 24(sp)
    ld t3, 32(sp)
    ld a2, 56(sp)
    ld a6, 64(sp)
    ld a7, 72(sp)
    addi sp, sp, 128
    mret
mtrap_unexpected:
    j mtrap_unexpected

.section .bss
.space 8192
fw_stack_top:

# Differential self-test for the Phase 1 RV64IM core: runs identically,
# unmodified, under our own riscv-emu and under real qemu-system-riscv64
# -M virt -bios none. Both must print byte-identical UART output; any
# difference is a bug in riscv-emu (QEMU is the reference).
#
# .option norvc: the C (compressed instruction) extension is not implemented
# yet, so every instruction here must be the full 32-bit encoding. Without
# this, the assembler is free to emit 16-bit compressed forms for anything
# that fits, which our decoder would misread as a different 32-bit
# instruction entirely.
.option norvc

.equ UART_BASE,  0x10000000
.equ TESTDEV,    0x00100000
.equ CLINT_BASE,     0x02000000
.equ CLINT_MTIMECMP, 0x02004000   # CLINT_BASE + 0x4000, one hart

# Sv39 PTE flag bits (V=0, R=1, W=2, X=3, U=4, G=5, A=6, D=7).
.equ PTE_V,            1
.equ PTE_LEAF_RWXAD,   0xCF   # V|R|W|X|A|D, no U: a kernel-only (S-mode) page
.equ ALIAS_VA,     0x140000000   # VPN[2]=5: deliberately not identity-mapped
.equ UNMAPPED_VA,  0x200000000   # VPN[2]=8: root_pt[8] is never written

.section .text
.global _start
_start:
    la sp, stack_top
    li s0, 0                       # failure counter, preserved across all cases

    la t0, mtrap_handler
    csrw mtvec, t0
    la t0, strap_handler
    csrw stvec, t0

    # mie.MTIE can only be set from M-mode (mie's own min-privilege), and
    # once this test drops to S-mode in test L it never returns to M except
    # through an interrupt - so this must happen right here, before
    # anything else, exactly like real firmware enables the timer once at
    # boot and leaves it enabled forever after. Disarming mtimecmp *first*
    # matters: its reset value is implementation-defined, and enabling
    # MTIE before disarming would let the interrupt fire immediately if it
    # happened to reset already-expired - long before test O is ready for
    # it, and in the middle of test L's own unrelated setup.
    li t0, -1
    li t1, CLINT_MTIMECMP
    sd t0, 0(t1)
    li t0, 1 << 7
    csrs mie, t0

    # --- 1: ADDI -------------------------------------------------------
    li t0, 5
    addi t0, t0, 10
    li t1, 15
    xor t2, t0, t1
    seqz a1, t2                    # a1 = 1 if equal (pass)
    li a0, '1'
    jal ra, report

    # --- 2: SUB going negative, detected via SLTI -----------------------
    li t0, 3
    li t1, 10
    sub t0, t0, t1                 # t0 = -7
    slti t2, t0, 0                 # signed: -7 < 0 -> 1
    mv a1, t2
    li a0, '2'
    jal ra, report

    # --- 3: SLTU treats all-ones as a huge unsigned value ---------------
    li t0, -1                      # 0xFFFFFFFFFFFFFFFF
    li t1, 1
    sltu t2, t1, t0                # unsigned: 1 < huge -> 1
    mv a1, t2
    li a0, '3'
    jal ra, report

    # --- 4: SRAI keeps the sign bit, SRLI does not ----------------------
    li t0, -8
    srai t1, t0, 1                 # arithmetic: -8 >> 1 = -4
    li t2, -4
    xor t3, t1, t2                 # 0 iff the arithmetic shift matched
    srli t4, t0, 63                # logical: shifting -8's top (sign) bit
                                    # down to bit 0 must yield exactly 1,
                                    # proving no sign bit was carried in
    xori t4, t4, 1
    or t3, t3, t4
    seqz a1, t3
    li a0, '4'
    jal ra, report

    # --- 5: ADDIW sign-extends its 32-bit result into all 64 bits ------
    li t0, 0x7fffffff
    addiw t1, t0, 1                 # 32-bit overflow: 0x7fffffff + 1 = 0x80000000,
                                     # sign-extended to 0xffffffff80000000
    li t2, 0xffffffff80000000
    xor t3, t1, t2
    seqz a1, t3
    li a0, '5'
    jal ra, report

    # --- 6: branches distinguish signed vs unsigned correctly -----------
    li t0, -1                       # as signed: -1 (less than 1); as unsigned: huge
    li t1, 1
    li t2, 0
    blt t0, t1, 6f                  # signed: -1 < 1, must be taken
    j fail6
6:  li t2, 1
fail6:
    bltu t0, t1, fail6b             # unsigned: huge < 1 is false, must NOT be taken
    j ok6b
fail6b:
    li t2, 0
ok6b:
    mv a1, t2
    li a0, '6'
    jal ra, report

    # --- 7: JAL/JALR round-trip through a real subroutine call ----------
    li t0, 41
    mv a2, t0
    jal ra, add_one                 # a0 reused as arg/result to keep it simple below
    li t1, 42
    xor t2, a0, t1
    seqz a1, t2
    li a0, '7'
    jal ra, report

    # --- 8: AUIPC computes a correct PC-relative address -----------------
8:  auipc t0, 0                     # t0 = address of this very instruction
    la t1, 8b
    xor t2, t0, t1
    seqz a1, t2
    li a0, '8'
    jal ra, report

    # --- 9: byte/halfword load sign- and zero-extension ------------------
    la t0, scratch
    li t1, 0x80
    sb t1, 0(t0)
    lb t2, 0(t0)                    # sign-extend 0x80 -> -128
    li t3, -128
    xor t4, t2, t3
    lbu t5, 0(t0)                   # zero-extend 0x80 -> 128
    li t6, 128
    xor t6, t5, t6
    or t4, t4, t6
    seqz a1, t4
    li a0, '9'
    jal ra, report

    # --- A: LW sign-extends, LWU zero-extends (RV64-only distinction) ---
    la t0, scratch
    li t1, 0x80000000
    sw t1, 0(t0)
    lw t2, 0(t0)                    # sign-extend -> 0xffffffff80000000
    li t3, 0xffffffff80000000
    xor t4, t2, t3
    lwu t5, 0(t0)                   # zero-extend -> 0x0000000080000000
    li t6, 0x80000000
    xor t6, t5, t6
    or t4, t4, t6
    seqz a1, t4
    li a0, 'A'
    jal ra, report

    # --- B/C: MUL/MULH on a product that overflows 64 bits ---------------
    li t0, 0x100000000              # 2^32
    mul t1, t0, t0                  # low 64 bits of 2^64 -> 0
    mulh t2, t0, t0                 # high 64 bits of 2^64 (signed*signed) -> 1
    seqz a1, t1                     # low word must be exactly 0
    li a0, 'B'
    jal ra, report
    li t4, 1
    xor t4, t2, t4
    seqz a1, t4                     # high word must be exactly 1
    li a0, 'C'
    jal ra, report

    # --- D: DIV/REM by zero (spec-defined, not a trap) -------------------
    li t0, 7
    li t1, 0
    div t2, t0, t1                  # DIV by zero -> -1
    li t3, -1
    xor t3, t2, t3
    divu t4, t0, t1                 # DIVU by zero -> all-ones (same bit pattern as -1)
    li t6, -1
    xor t4, t4, t6
    rem t5, t0, t1                  # REM by zero -> dividend unchanged
    xor t5, t5, t0
    or t3, t3, t4
    or t3, t3, t5
    seqz a1, t3
    li a0, 'D'
    jal ra, report

    # --- E: signed overflow case INT64_MIN / -1 does not trap ------------
    li t0, 1
    slli t0, t0, 63                  # t0 = INT64_MIN (0x8000000000000000)
    li t1, -1
    div t2, t0, t1                   # spec: result is the dividend, unchanged
    xor t2, t2, t0
    rem t3, t0, t1                   # spec: remainder is 0
    or t2, t2, t3
    seqz a1, t2
    li a0, 'E'
    jal ra, report

    # --- F: DIVW/REMW word-size edge case, INT32_MIN / -1 ----------------
    li t0, 0x80000000                # INT32_MIN once truncated to 32 bits
    li t1, -1
    divw t2, t0, t1                  # spec: result is the (sign-extended) dividend
    li t3, 0xffffffff80000000
    xor t3, t2, t3
    remw t4, t0, t1                  # spec: remainder is 0
    or t3, t3, t4
    seqz a1, t3
    li a0, 'F'
    jal ra, report

    # --- G: CSRRW/CSRRS/CSRRC on mscratch (a plain, side-effect-free
    # read/write register - safe to use as a CSR-semantics playground) -----
    li t0, 0x1234
    csrw mscratch, t0
    csrr t1, mscratch
    li t2, 0x1234
    xor t2, t1, t2                  # 0 iff the plain write/read matched

    li t0, 0x0F0F
    csrrs x0, mscratch, t0          # set bits; rd=x0 means "discard old value"
    csrr t1, mscratch
    li t3, 0x1F3F                   # 0x1234 | 0x0F0F
    xor t3, t1, t3
    or t2, t2, t3

    li t0, 0x0F0F
    csrrc x0, mscratch, t0          # clear those bits: (A|B)&~B == A&~B, which
                                     # is 0x1030, NOT the original 0x1234 - any
                                     # bits A and B both had are gone for good.
    csrr t1, mscratch
    li t4, 0x1030
    xor t4, t1, t4
    or t2, t2, t4
    seqz a1, t2
    li a0, 'G'
    jal ra, report

    # --- H: CSRRWI/CSRRSI/CSRRCI (immediate forms) -------------------------
    csrwi mscratch, 5
    csrr t1, mscratch
    li t2, 5
    xor t2, t1, t2

    csrsi mscratch, 8               # 5 | 8 = 13
    csrr t1, mscratch
    li t3, 13
    xor t3, t1, t3
    or t2, t2, t3

    csrci mscratch, 1               # 13 & ~1 = 12
    csrr t1, mscratch
    li t4, 12
    xor t4, t1, t4
    or t2, t2, t4

    csrrs x0, mscratch, x0          # rs1=x0: must NOT write (no-op read)
    csrr t1, mscratch
    li t5, 12
    xor t5, t1, t5
    or t2, t2, t5
    seqz a1, t2
    li a0, 'H'
    jal ra, report

    # --- I: ECALL from M-mode traps to mtvec and MRET returns correctly ---
    la t0, ecall_m_seen
    sb x0, 0(t0)
    ecall
    la t0, ecall_m_seen
    lbu a1, 0(t0)                   # the handler set this to 1
    li a0, 'I'
    jal ra, report

    # --- J: an illegal instruction (a deliberately undefined OP-IMM-32
    # funct3=3 encoding) traps with mcause=2 and mtval holding the raw
    # instruction bits -----------------------------------------------------
    la t0, illegal_seen
    sb x0, 0(t0)
    .word 0x0000301b                # opcode=0x1B, funct3=3: not SLLIW/SRLIW/
                                     # SRAIW/ADDIW, so it is illegal by spec
    la t0, illegal_seen
    lbu a1, 0(t0)
    li a0, 'J'
    jal ra, report

    # --- K: EBREAK traps with mcause=3 -------------------------------------
    la t0, ebreak_seen
    sb x0, 0(t0)
    ebreak
    la t0, ebreak_seen
    lbu a1, 0(t0)
    li a0, 'K'
    jal ra, report

    # --- L: delegated ECALL-from-S-mode: medeleg routes it to stvec instead
    # of mtvec, MRET can hand off into S-mode, and SRET returns to it -------
    li t0, 1 << 9                   # medeleg bit 9 = "ECALL from S-mode"
    csrw medeleg, t0
    la t0, ecall_s_seen
    sb x0, 0(t0)

    # Real M-mode firmware always configures PMP before ever dropping into
    # S-mode: with zero active regions, hardware denies every S/U-mode
    # fetch and memory access by default. One NAPOT region spanning the
    # entire address space, fully permissive, is the simplest firmware
    # convention (and enough for S-mode here to run code, touch the stack,
    # and reach the UART/test-finisher MMIO after the SRET below).
    li t0, -1                       # pmpaddr0 = all-ones: NAPOT "match everything"
    csrw pmpaddr0, t0
    li t0, 0x1F                     # R=1 W=1 X=1 A=NAPOT(3) L=0
    csrw pmpcfg0, t0

    la t0, s_mode_code
    csrw mepc, t0
    li t1, 1 << 11                  # mstatus.MPP = 01 (S), bits [12:11]
    csrs mstatus, t1
    mret                            # drops into S-mode at s_mode_code

after_s_mode:
    la t0, ecall_s_seen
    lbu a1, 0(t0)
    li a0, 'L'
    jal ra, report

    # --- S: CSR privilege enforcement, still right where L left off (S-
    # mode). An M-mode-only CSR access from S - `csrr t0, mstatus`, read-
    # only, no different from a write for this check - must trap illegal
    # (mcause=2), not silently succeed and not silently resume past the
    # fault at the wrong privilege. That second failure mode is a real bug
    # this project's own F/D differential test against real QEMU
    # surfaced (see test R's own comment and the README): do_csr's
    # illegal-instruction path raised the trap correctly, but cpu_step's
    # own SYSTEM-opcode case then fell through to its unconditional `pc =
    # next_pc` advance instead of returning immediately, overwriting the
    # trap's redirect into mtrap_handler - so execution just continued
    # normally, at the wrong privilege, as if the read had worked.
    # illegal_seen is set only by mtrap_handler's own cause==2 case, so
    # checking it here is exactly checking mcause==2, not inferring it.
    # Two more checks guard against a partial fix of the same bug class
    # reappearing differently, rather than just re-confirming this exact
    # symptom: mstatus.MPP must have recorded S (1), not M, as the
    # privilege this trap came from (mtrap_handler captures it into
    # illegal_mpp_seen - see its own comment); and t0, the faulting
    # instruction's own destination register, poisoned with a sentinel
    # first, must still hold that sentinel afterward - a CSR access that
    # traps must never have written its destination, illegal_seen or not.
    la t1, illegal_seen
    sb x0, 0(t1)
    la t1, illegal_mpp_seen
    sb x0, 0(t1)
    li t0, 0xdeadbeef                # sentinel: must survive the faulting csrr untouched
    csrr t0, mstatus
    li t2, 0xdeadbeef
    xor t2, t0, t2
    seqz a2, t2                      # a2 = 1 iff t0 was never written
    la t1, illegal_seen
    lbu a1, 0(t1)                    # a1 = 1 iff mtrap_handler's cause==2 case ran
    and a1, a1, a2
    la t1, illegal_mpp_seen
    lbu t3, 0(t1)
    li t4, 1                         # expected MPP = S
    xor t4, t3, t4
    seqz t4, t4
    and a1, a1, t4
    li a0, 'S'
    jal ra, report

    # --- M: Sv39 paging. Real Linux sets this up itself from S-mode (no
    # M-mode help needed - satp's minimum privilege is S), so this continues
    # right where L left off. Two 1GB identity superpages cover UART+
    # test-finisher and our own RAM; a genuine 3-level walk then maps a
    # non-identity virtual address to a scratch physical page. Writing
    # through that alias VA and reading the same byte back through the
    # identity mapping proves the walk computed a real translation, not a
    # passthrough - a VA==PA coincidence could not produce this result.
    la t0, root_pt
    la t1, l1_pt
    la t2, l0_pt
    la t3, test_page

    li t5, PTE_LEAF_RWXAD
    sd t5, 0(t0)                     # root_pt[0]: superpage -> physical [0, 1GB)

    li t5, 0x80000                  # PPN of physical base 0x80000000 (addr>>12)
    slli t5, t5, 10
    ori t5, t5, PTE_LEAF_RWXAD
    sd t5, 16(t0)                    # root_pt[2]: superpage -> physical [0x80000000,..)

    srli t5, t1, 12                 # root_pt[5] -> l1_pt (non-leaf pointer)
    slli t5, t5, 10
    ori t5, t5, PTE_V
    sd t5, 40(t0)                    # index 5 * 8 bytes

    srli t5, t2, 12                 # l1_pt[0] -> l0_pt (non-leaf pointer)
    slli t5, t5, 10
    ori t5, t5, PTE_V
    sd t5, 0(t1)

    srli t5, t3, 12                 # l0_pt[0] -> test_page (leaf)
    slli t5, t5, 10
    ori t5, t5, PTE_LEAF_RWXAD
    sd t5, 0(t2)

    srli t5, t0, 12                 # satp = MODE(Sv39=8) | PPN(root_pt)
    li t6, 8
    slli t6, t6, 60
    or t5, t5, t6
    csrw satp, t5
    sfence.vma

    li t5, ALIAS_VA
    li t6, 0xCAFEBABE
    sw t6, 0(t5)                     # write through the non-identity alias
    lw a1, 0(t3)                     # read back through the identity mapping;
                                      # LW sign-extends, but t6's own low-32
                                      # value (from lui+slli+addi) never got
                                      # sign-extended, so clear a1's upper 32
                                      # bits before comparing like-for-like.
    slli a1, a1, 32
    srli a1, a1, 32
    xor a1, a1, t6
    seqz a1, a1
    li a0, 'M'
    jal ra, report

    # --- N: an access to a genuinely unmapped VA (root_pt[8], untouched by
    # anything above) raises a page fault. It is not delegated - medeleg's
    # bit 13 was never set - so mtrap_handler sees it and MRETs back to S
    # (MPP recorded S at trap entry, same as every other trap this session). -
    la t0, pagefault_seen
    sb x0, 0(t0)
    li t5, UNMAPPED_VA
    lw t6, 0(t5)
    la t0, pagefault_seen
    lbu a1, 0(t0)
    li a0, 'N'
    jal ra, report

    # --- O: a CLINT timer interrupt, routed through M-mode on its first
    # occurrence (MTI is never delegated - mideleg bit 7 is never set for
    # it, so an undelegated interrupt always targets M), whose handler
    # disarms the timer, converts it into a pending supervisor timer
    # interrupt by writing mip directly (M-mode may write any bit of mip;
    # only S-mode's restricted "sip" view is masked), and delegates STI
    # (bit 5) from then on - after which the delegation mechanism already
    # built in Phase 2 delivers it straight to S-mode with no further help.
    # This is exactly the pattern real SBI firmware uses on any RISC-V
    # machine without the newer Sstc extension.
    la t0, mti_in_m_seen
    sb x0, 0(t0)
    la t0, stip_seen
    sb x0, 0(t0)

    li t0, CLINT_MTIMECMP
    sd x0, 0(t0)             # mtimecmp = 0: already expired, fires at once

    li t0, 1 << 5            # sie.STIE
    csrs sie, t0
    li t0, 1 << 1            # sstatus.SIE
    csrs sstatus, t0

    li t4, 0
1:  la t0, stip_seen
    lbu t1, 0(t0)
    bnez t1, 2f
    addi t4, t4, 1
    li t5, 1000000           # a generous bound: this must resolve within a
    blt t4, t5, 1b           # handful of instructions, not loop forever
2:
    la t0, mti_in_m_seen
    lbu a1, 0(t0)
    la t0, stip_seen
    lbu t2, 0(t0)
    and a1, a1, t2
    li a0, 'O'
    jal ra, report

    # --- U: WFI must honor the real wake condition - mip & mie, locally
    # enabled - even while the GLOBAL interrupt-enable bit (sstatus.SIE)
    # is clear. The global-enable bit only decides whether a pending,
    # locally-enabled interrupt is actually DELIVERED as a trap; it must
    # not also gate whether wfi is allowed to stop waiting. Getting this
    # backwards - requiring sstatus.SIE too, the same condition that
    # decides trap delivery - is a real deadlock, not a theoretical one:
    # software is free to execute wfi with its own global enable
    # momentarily clear, fully intending to resume at the very next
    # instruction and re-enable it itself; a wfi that insists on the
    # global-enable-gated condition can then sleep forever right there,
    # since nothing is left running to ever flip that bit back on. This
    # is exactly what a real Linux guest's idle hart hit once the
    # emulator's real-time mode made wfi actually sleep instead of
    # spinning (see the real-time mtime/WFI work's own notes) - the
    # guest's own scheduler tick stopped arriving until an unrelated
    # external event forced a wake through a different path, which
    # surfaced as a kernel-side RCU stall. This test reproduces the exact
    # precondition that bug needs (STI pending + enabled, SIE clear) and
    # checks both halves: wfi must resume WITHOUT the trap firing while
    # SIE is 0, and the already-pending, already-enabled interrupt must
    # still be taken promptly once SIE is set.
    #
    # Continues right where O left off (sie.STIE and STI's own mideleg
    # delegation are already set up there) - only sstatus.SIE and
    # mtimecmp need rearming here. Arming mtimecmp=0 traps to M
    # unconditionally regardless of S's own SIE (MTI itself, bit 7, is
    # still undelegated - mideleg bit 7 is never set in this file - and an
    # undelegated interrupt targeting a strictly higher privilege than
    # the one currently running is never masked by that lower privilege's
    # own status bits, only by M's own mstatus.MIE, which does not apply
    # from S); M's handler (mtrap_handler case 5) converts it into a
    # pending STI via mip directly, the same conversion O's own first
    # occurrence already relies on. That conversion is therefore not what
    # this test is checking - mti_in_m_seen just confirms the precondition
    # (STI now pending+enabled) is actually in place before the real
    # check (stip_seen, strap_handler's own STI case) begins.
    la t0, mti_in_m_seen
    sb x0, 0(t0)
    la t0, stip_seen
    sb x0, 0(t0)

    csrc sstatus, 2                   # sstatus.SIE (bit 1) = 0
    li t0, CLINT_MTIMECMP
    sd x0, 0(t0)                      # expired - traps to M unconditionally,
                                       # not gated by S's own SIE at all

    li t4, 0                          # wait for the MTI->M round trip only -
1:  la t0, mti_in_m_seen               # mti_in_m_seen, not stip_seen: M's
    lbu t1, 0(t0)                      # handler runs regardless of S's SIE,
    bnez t1, 2f                        # unlike the S-mode STI trap this test
    addi t4, t4, 1                     # means to prevent below
    li t5, 1000000
    blt t4, t5, 1b
2:
    la t0, stip_seen
    lbu a2, 0(t0)                     # must be 0: STI is pending+enabled but
                                       # SIE is 0, so the S-trap must not have
                                       # fired yet
    wfi                                # must return here (mip & mie already
                                       # both hold for STI) without taking
                                       # the trap, since sstatus.SIE is 0
    la t0, stip_seen
    lbu a3, 0(t0)                     # must STILL be 0: wfi resumed, but the
                                       # trap must not have been taken while
                                       # SIE was 0

    csrs sstatus, 2                   # now enable SIE - the already-pending,
                                       # already-enabled STI must be taken
                                       # promptly
    li t4, 0
3:  la t0, stip_seen
    lbu t1, 0(t0)
    bnez t1, 4f
    addi t4, t4, 1
    li t5, 1000000
    blt t4, t5, 3b
4:
    la t0, stip_seen
    lbu a4, 0(t0)                     # must be 1 now: the trap finally fired

    seqz a2, a2                       # 1 iff a2 was 0 (pass)
    seqz a3, a3                       # 1 iff a3 was 0 (pass)
    and a1, a2, a3
    and a1, a1, a4                    # a4 itself: 1 means pass already
    li a0, 'U'
    jal ra, report

    # --- P: the A extension - LR/SC round-trips correctly, a failed SC
    # (reservation already gone) correctly reports failure without writing,
    # and AMOADD/AMOSWAP perform their read-modify-write atomically from a
    # single-hart interpreter's point of view (no real concurrency to get
    # wrong, but the *bookkeeping* - old value in rd, new value in memory -
    # is exactly what Linux's spinlock/atomic_t implementation depends on). -
    .option push
    .option arch, +a
    la t0, scratch
    li t1, 100
    sd t1, 0(t0)

    lr.d t2, (t0)                    # reservation established
    li t3, 1
    add t2, t2, t3
    sc.d t4, t2, (t0)                # should succeed: t4 = 0
    ld t5, 0(t0)                     # memory should now read 101
    li t6, 101
    xor t5, t5, t6
    or t5, t5, t4                    # t4 must be exactly 0 too
    seqz a1, t5

    sc.d t4, t2, (t0)                # no LR since the last SC: must fail
    xor t4, t4, t3                   # t3 is still 1; fail code is also 1
    seqz t4, t4
    and a1, a1, t4

    li t1, 5
    amoadd.d t2, t1, (t0)            # old value (101) returned in t2
    li t3, 101
    xor t3, t2, t3
    ld t5, 0(t0)                     # memory now 106
    li t6, 106
    xor t5, t5, t6
    or t3, t3, t5
    seqz t3, t3
    and a1, a1, t3

    li t1, 0xBEEF
    amoswap.w t2, t1, (t0)           # old low word (106) returned in t2
    li t3, 106
    xor t3, t2, t3
    lw t5, 0(t0)
    li t6, 0xBEEF
    xor t5, t5, t6
    or t3, t3, t5
    seqz t3, t3
    and a1, a1, t3
    .option pop
    li a0, 'P'
    jal ra, report

    # --- Q: the C extension - compressed forms expand to exactly the base
    # instruction they are defined as an alias for. Using the explicit
    # "c.xxx" mnemonics (rather than relying on the assembler's own
    # auto-compression heuristics under .option rvc) pins down precisely
    # which 16-bit encodings this exercises. ------------------------------
    .option push
    .option arch, +c
    li s2, 0                         # s2: this test's own fail counter

    c.li t0, 5
    c.addi t0, 10                    # t0 = 15
    li t1, 15
    xor t1, t0, t1
    bnez t1, 1f
    j 2f
1:  addi s2, s2, 1
2:
    c.mv t2, t0                      # t2 = 15
    xor t2, t2, t0
    bnez t2, 3f
    j 4f
3:  addi s2, s2, 1
4:
    li a2, 8                         # a2/s1 are in the x8-x15 range c.* needs
    li s1, 100
    c.add s1, a2                     # s1 = 108
    li t3, 108
    xor t3, s1, t3
    c.sub s1, a2                     # s1 = 100 again
    li t4, 100
    xor t4, s1, t4
    or t3, t3, t4
    bnez t3, 5f
    j 6f
5:  addi s2, s2, 1
6:
    la t0, scratch
    li a3, 0x1234                    # C.SW/C.LW/C.SD/C.LD need BOTH the base
    mv a2, t0                        # and the data register in x8-x15
    c.sw a3, 0(a2)
    c.lw a4, 0(a2)
    xor a4, a4, a3
    li a3, 0x56789abc
    c.sd a3, 0(a2)
    c.ld a5, 0(a2)
    xor a5, a5, a3
    or a4, a4, a5
    bnez a4, 7f
    j 8f
7:  addi s2, s2, 1
8:
    li t3, 0
    c.j 10f
    addi t3, t3, 1                   # must be skipped
10:
    li t4, 0
    mv a2, x0
    c.beqz a2, 11f
    addi t4, t4, 1
11:
    li a2, 1
    c.bnez a2, 12f
    addi t4, t4, 1
12:
    or t3, t3, t4
    bnez t3, 9f
    j 13f
9:  addi s2, s2, 1
13:
    c.lui t3, 3                      # t3 = 0x3000
    li t4, 0x3000
    xor t4, t3, t4
    c.addi16sp sp, -32
    mv t5, sp                        # t5 = sp - 32
    c.addi16sp sp, 32                # sp restored to its original value
    sub t5, sp, t5                   # sp - (sp - 32) = 32, not -32
    li t6, 32
    xor t6, t5, t6
    or t4, t4, t6
    bnez t4, 14f
    j 15f
14: addi s2, s2, 1
15:
    li t3, 1
    c.slli t3, 8                     # t3 = 0x100 (full rd field: any register)
    li t4, 0x100
    xor t4, t3, t4
    li a3, 0x40                      # C.SRLI's rd' DOES need x8-x15
    c.srli a3, 2                     # a3 = 0x10
    li t5, 0x10
    xor t5, a3, t5
    or t4, t4, t5
    li a2, 0xFF
    c.andi a2, 0x0F                  # a2 = 0x0F
    li t5, 0x0f
    xor t5, a2, t5
    or t4, t4, t5
    bnez t4, 16f
    j 17f
16: addi s2, s2, 1
17:
    la t0, q_call_target
    c.jalr t0                        # the callee itself returns via c.jr
    li t3, 42
    xor t3, a0, t3
    bnez t3, 18f
    j 19f
18: addi s2, s2, 1
19:
    .option pop
    seqz a1, s2
    li a0, 'Q'
    jal ra, report

    # --- R: F/D (floating point) - the Phase 5 feature this project's own
    # README flagged as never having a differential test, only "a real
    # kernel's libc-emitted fld/fsd and busybox's own arithmetic didn't
    # visibly misbehave" (see README's "Phase 5, done"). Covers every FP
    # opcode group cpu.c implements - arithmetic, FMADD-family, compare,
    # min/max, convert, classify, sign-injection - plus NaN-boxing,
    # fcsr/frm round-tripping, the four compressed FP forms, and
    # mstatus.FS gating. GUEST_FLAGS' -march=rv64im has neither F nor D
    # (nor C); `.option arch` adds them for this block only, the same
    # technique test Q above already uses for the C extension alone. Every
    # case below checks an exact bit pattern (via FSD + an integer load),
    # not "looks about right" - the same reason every other test in this
    # file XORs against a known-correct constant rather than eyeballing a
    # printed value.
    #
    # Positioned after test Q, not alphabetically before test O: test L
    # (far above) drops to S-mode and - by its own design, see its own
    # comment - never returns to M except through an interrupt, and this
    # file's own differential testing against real QEMU (see test Z below
    # and the README) found that it genuinely never does return: test O's
    # own MTI handler MRETs back to whatever priv was live when the timer
    # fired - S, since that's what's running by then - not M. Every test
    # from L through here, including this one, therefore actually runs in
    # S-mode on real hardware. FS lives in `mstatus`, an M-mode-only CSR,
    # but `sstatus` is a window onto the SAME bits (see its own
    # `SSTATUS_MASK` in cpu.c) that S-mode can legally write - so this
    # enables FS through `sstatus` instead, which works regardless of
    # which privilege level is actually live here, sidestepping the
    # question entirely rather than fighting it.
    .option push
    .option arch, +f, +d, +c
    li t0, 3 << 13                   # sstatus.FS = Dirty - defaults to Off,
    csrs sstatus, t0                 # and every FP opcode below needs FS != Off
    li s2, 0                         # this test's own fail counter

    # FADD.D: 2.5 + 1.5 = 4.0
    la t0, dbl_2_5
    fld f0, 0(t0)
    la t0, dbl_1_5
    fld f1, 0(t0)
    fadd.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_4_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 1f
    j 2f
1:  addi s2, s2, 1
2:

    # FSUB.D: 10.0 - 3.0 = 7.0
    la t0, dbl_10_0
    fld f0, 0(t0)
    la t0, dbl_3_0
    fld f1, 0(t0)
    fsub.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_7_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 3f
    j 4f
3:  addi s2, s2, 1
4:

    # FMUL.D: 2.0 * 3.0 = 6.0
    la t0, dbl_2_0
    fld f0, 0(t0)
    la t0, dbl_3_0
    fld f1, 0(t0)
    fmul.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_6_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 5f
    j 6f
5:  addi s2, s2, 1
6:

    # FDIV.D: 10.0 / 4.0 = 2.5
    la t0, dbl_10_0
    fld f0, 0(t0)
    la t0, dbl_4_0
    fld f1, 0(t0)
    fdiv.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_2_5
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 7f
    j 8f
7:  addi s2, s2, 1
8:

    # FSQRT.D: sqrt(16.0) = 4.0
    la t0, dbl_16_0
    fld f0, 0(t0)
    fsqrt.d f2, f0
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_4_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 9f
    j 10f
9:  addi s2, s2, 1
10:

    # FMADD.D: (2.0*3.0)+1.0 = 7.0
    la t0, dbl_2_0
    fld f0, 0(t0)
    la t0, dbl_3_0
    fld f1, 0(t0)
    la t0, dbl_1_0
    fld f2, 0(t0)
    fmadd.d f3, f0, f1, f2
    la t0, scratch
    fsd f3, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_7_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 11f
    j 12f
11: addi s2, s2, 1
12:

    # FMSUB.D: (2.0*3.0)-1.0 = 5.0
    la t0, dbl_2_0
    fld f0, 0(t0)
    la t0, dbl_3_0
    fld f1, 0(t0)
    la t0, dbl_1_0
    fld f2, 0(t0)
    fmsub.d f3, f0, f1, f2
    la t0, scratch
    fsd f3, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_5_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 13f
    j 14f
13: addi s2, s2, 1
14:

    # FEQ.D/FLT.D/FLE.D: 1.0 vs 2.0
    la t0, dbl_1_0
    fld f0, 0(t0)
    la t0, dbl_2_0
    fld f1, 0(t0)
    feq.d a0, f0, f1                 # 1.0 == 2.0 -> 0
    flt.d a1, f0, f1                 # 1.0 <  2.0 -> 1
    fle.d a2, f0, f1                 # 1.0 <= 2.0 -> 1
    xori a0, a0, 0
    xori a1, a1, 1
    xori a2, a2, 1
    or a0, a0, a1
    or a0, a0, a2
    bnez a0, 15f
    j 16f
15: addi s2, s2, 1
16:

    # FMIN.D/FMAX.D: min/max(3.0, 5.0)
    la t0, dbl_3_0
    fld f0, 0(t0)
    la t0, dbl_5_0
    fld f1, 0(t0)
    fmin.d f2, f0, f1
    fmax.d f3, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    fsd f3, 8(t0)
    ld t1, 0(t0)
    ld t3, 8(t0)
    la t0, dbl_3_0
    ld t2, 0(t0)
    la t0, dbl_5_0
    ld t4, 0(t0)
    xor t1, t1, t2
    xor t3, t3, t4
    or t1, t1, t3
    bnez t1, 17f
    j 18f
17: addi s2, s2, 1
18:

    # FCVT.D.W / FCVT.W.D round trip: 42 -> 42.0 -> 42 (rtz: exact anyway,
    # pinned down so a later frm change below cannot affect this case).
    li t5, 42
    fcvt.d.w f0, t5
    fcvt.w.d a3, f0, rtz
    li t6, 42
    xor t6, a3, t6
    la t0, scratch
    fsd f0, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_42_0
    ld t2, 0(t0)
    xor t1, t1, t2
    or t6, t6, t1
    bnez t6, 19f
    j 20f
19: addi s2, s2, 1
20:

    # FCLASS.D: +infinity classifies as bit 7 (0x80)
    la t0, dbl_pos_inf
    fld f0, 0(t0)
    fclass.d a4, f0
    li a5, 0x80
    xor a4, a4, a5
    bnez a4, 21f
    j 22f
21: addi s2, s2, 1
22:

    # FSGNJ.D/FSGNJN.D/FSGNJX.D: magnitude of 3.0, sign from -5.0
    la t0, dbl_3_0
    fld f0, 0(t0)
    la t0, dbl_neg_5_0
    fld f1, 0(t0)
    fsgnj.d  f2, f0, f1               # sign of f1 (neg) + |f0|  = -3.0
    fsgnjn.d f3, f0, f1               # negated sign of f1 (pos) + |f0| = +3.0
    fsgnjx.d f4, f0, f1               # sign(f0) xor sign(f1) (neg) + |f0| = -3.0
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_neg_3_0
    ld t2, 0(t0)
    xor t1, t1, t2
    la t0, scratch
    fsd f3, 0(t0)
    ld t3, 0(t0)
    la t0, dbl_3_0
    ld t4, 0(t0)
    xor t3, t3, t4
    la t0, scratch
    fsd f4, 0(t0)
    ld t5, 0(t0)
    la t0, dbl_neg_3_0
    ld t6, 0(t0)
    xor t5, t5, t6
    or t1, t1, t3
    or t1, t1, t5
    bnez t1, 23f
    j 24f
23: addi s2, s2, 1
24:

    # NaN-boxing: a single-precision load (FLW) must leave the upper 32
    # bits of the 64-bit register all-ones, per spec - checked directly via
    # FSD + an integer load of each half, not just that arithmetic on the
    # value happens to still work afterward.
    la t0, flt_1_5
    flw f0, 0(t0)
    la t0, scratch
    fsd f0, 0(t0)
    lwu t1, 4(t0)                     # upper 32 bits (little-endian)
    li t2, 0xFFFFFFFF
    xor t1, t1, t2
    lwu t3, 0(t0)                     # lower 32 bits: the real float bits
    li t4, 0x3FC00000
    xor t3, t3, t4
    or t1, t1, t3
    bnez t1, 25f
    j 26f
25: addi s2, s2, 1
26:

    # fcsr/frm/fflags round-trip. Not exercised by accumulation from real
    # arithmetic above - this project's own documented simplification (see
    # README's "Phase 5, done") is that fflags are never actually set by
    # the arithmetic itself - so this only confirms the CSR's own
    # storage/masking is correct, not that any FP op updates it.
    li t0, 0x5F                       # frm=2 (bits 7:5), fflags=0x1F (bits 4:0)
    csrw fcsr, t0
    csrr t1, fcsr
    xor t1, t1, t0
    bnez t1, 27f
    j 28f
27: addi s2, s2, 1
28:

    # Compressed C.FLD/C.FSD: base register must be x8-x15 (a2 qualifies),
    # and the FP register field is compressed too - f8-f15 only (f8/f9
    # used here), unlike the base register's own uncompressed load below.
    la a2, scratch
    la t0, dbl_7_0
    fld f8, 0(t0)
    c.fsd f8, 0(a2)
    c.fld f9, 0(a2)
    fsd f9, 0(a2)
    ld t1, 0(a2)
    la t0, dbl_7_0
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 29f
    j 30f
29: addi s2, s2, 1
30:

    # Compressed C.FLDSP/C.FSDSP: implicit sp base, any f0-f31 destination.
    # Carves out its own 16 bytes rather than reusing "scratch", the same
    # discipline every other sp-relative access in this file follows.
    addi sp, sp, -16
    la t0, dbl_3_0
    fld f3, 0(t0)
    c.fsdsp f3, 0(sp)
    c.fldsp f4, 0(sp)
    fsd f4, 0(a2)                     # a2 still points at scratch
    ld t3, 0(a2)
    la t0, dbl_3_0
    ld t4, 0(t0)
    xor t3, t3, t4
    addi sp, sp, 16
    bnez t3, 31f
    j 32f
31: addi s2, s2, 1
32:

    # FCVT.W.D of NaN: must saturate to INT32_MAX (0x7FFFFFFF), not wrap to
    # C's own undefined-behavior cast result - this host's x86-64
    # cvttsd2si "integer indefinite" pattern, INT32_MIN - the exact real
    # bug this check (and cpu.c's matching fix) exists for: NaN/overflow
    # saturation is spec-mandated, and a plain `(int32_t)v` cast silently
    # gets it backwards for exactly these inputs.
    la t0, dbl_nan
    fld f0, 0(t0)
    fcvt.w.d a3, f0, rtz
    li t4, 0x7FFFFFFF
    xor t4, a3, t4
    bnez t4, 33f
    j 34f
33: addi s2, s2, 1
34:

    # FCVT.W.D of +Infinity: the same overflow saturation, approached from
    # a finite-but-out-of-range direction instead of NaN.
    la t0, dbl_pos_inf
    fld f0, 0(t0)
    fcvt.w.d a3, f0, rtz
    li t4, 0x7FFFFFFF
    xor t4, a3, t4
    bnez t4, 35f
    j 36f
35: addi s2, s2, 1
36:

    # FCVT.L.D of NaN: the same saturation again, 64-bit signed
    # destination (INT64_MAX this time, not INT32_MAX).
    la t0, dbl_nan
    fld f0, 0(t0)
    fcvt.l.d a3, f0, rtz
    la t0, dbl_int64_max
    ld t4, 0(t0)
    xor t4, a3, t4
    bnez t4, 37f
    j 38f
37: addi s2, s2, 1
38:

    # FMIN.D/FMAX.D with one NaN operand: must return the non-NaN operand
    # (IEEE 754-2008 minNum/maxNum semantics) - not NaN itself, and not
    # whichever operand simply happens to come first positionally.
    la t0, dbl_nan
    fld f0, 0(t0)
    la t0, dbl_5_0
    fld f1, 0(t0)
    fmin.d f2, f0, f1
    fmax.d f3, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    fsd f3, 8(t0)
    ld t1, 0(t0)
    ld t3, 8(t0)
    la t0, dbl_5_0
    ld t2, 0(t0)
    xor t1, t1, t2
    xor t3, t3, t2
    or t1, t1, t3
    bnez t1, 39f
    j 40f
39: addi s2, s2, 1
40:

    # FDIV.D by zero: 1.0 / 0.0 must be +Infinity, not a trap and not some
    # host-specific result - well-defined under IEEE 754 regardless of the
    # DZ flag this emulator doesn't track (see README's "Phase 5, done").
    la t0, dbl_1_0
    fld f0, 0(t0)
    la t0, dbl_0_0
    fld f1, 0(t0)
    fdiv.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_pos_inf
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 41f
    j 42f
41: addi s2, s2, 1
42:

    # Canonical NaN (0x7FF8000000000000 for double): any arithmetic
    # result that is NaN must be exactly this pattern, never whatever
    # sign/payload the host's own FPU happens to produce. 0.0/0.0 is the
    # simplest way to manufacture one from scratch - this host's SSE2
    # gives it a *set* sign bit (0xFFF8...) where real QEMU's softfloat
    # (and the spec) say clear.
    la t0, dbl_0_0
    fld f0, 0(t0)
    la t0, dbl_0_0
    fld f1, 0(t0)
    fdiv.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_nan
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 45f
    j 46f
45: addi s2, s2, 1
46:

    # Canonical NaN again, this time from an input operand that is
    # *already* NaN but with its own nonzero, non-canonical payload -
    # must still come out as the plain canonical pattern, payload
    # discarded, not propagated the way this host's FPU does it.
    la t0, dbl_nan_payload
    fld f0, 0(t0)
    la t0, dbl_1_0
    fld f1, 0(t0)
    fadd.d f2, f0, f1
    la t0, scratch
    fsd f2, 0(t0)
    ld t1, 0(t0)
    la t0, dbl_nan
    ld t2, 0(t0)
    xor t1, t1, t2
    bnez t1, 47f
    j 48f
47: addi s2, s2, 1
48:

    # FMIN.D/FMAX.D with signed zeros: the ISA manual pins down
    # FMIN(-0,+0)=-0 and FMAX(-0,+0)=+0 explicitly, a case IEEE 754-2008's
    # own minNum/maxNum leaves unspecified (+/-0 compare equal) - checked
    # via FMV.X.D rather than FEQ.D, since FEQ.D would see -0.0 and +0.0
    # as equal and miss a wrong-signed result entirely. This host's own
    # libm `fmin` does not reliably honor it (observed: picks +0 here).
    la t0, dbl_neg_0_0
    fld f0, 0(t0)
    la t0, dbl_0_0
    fld f1, 0(t0)
    fmin.d f2, f0, f1
    fmax.d f3, f0, f1
    fmv.x.d t1, f2
    fmv.x.d t3, f3
    la t0, dbl_neg_0_0
    ld t2, 0(t0)
    xor t1, t1, t2                   # fmin(-0,+0) must be exactly -0.0
    la t0, dbl_0_0
    ld t4, 0(t0)
    xor t3, t3, t4                   # fmax(-0,+0) must be exactly +0.0
    or t1, t1, t3
    bnez t1, 49f
    j 50f
49: addi s2, s2, 1
50:

    # FCVT.WU.D/FCVT.LU.D: the saturation fix only had direct test
    # coverage for the signed forms (FCVT.W.D/FCVT.L.D above) - the
    # unsigned ones have their own distinct saturation targets (0 for a
    # negative input, UINT32_MAX/UINT64_MAX for NaN or overflow, not
    # INT32_MAX/INT64_MAX) and FCVT.WU's 32-bit result is still
    # sign-extended into the 64-bit register on write-back per spec, so
    # UINT32_MAX (0xFFFFFFFF) must read back as all-ones, not
    # zero-extended.
    la t0, dbl_neg_5_0
    fld f0, 0(t0)
    fcvt.wu.d a3, f0, rtz
    bnez a3, 51f                     # negative input -> 0
    j 52f
51: addi s2, s2, 1
52:
    la t0, dbl_nan
    fld f0, 0(t0)
    fcvt.wu.d a3, f0, rtz
    la t0, dbl_uint32_max
    ld t4, 0(t0)                     # sign-extended 0xFFFFFFFF
    xor t4, a3, t4
    bnez t4, 53f
    j 54f
53: addi s2, s2, 1
54:
    la t0, dbl_neg_5_0
    fld f0, 0(t0)
    fcvt.lu.d a3, f0, rtz
    bnez a3, 55f                     # negative input -> 0
    j 56f
55: addi s2, s2, 1
56:
    la t0, dbl_nan
    fld f0, 0(t0)
    fcvt.lu.d a3, f0, rtz
    addi t4, x0, -1                  # UINT64_MAX
    xor t4, a3, t4
    bnez t4, 57f
    j 58f
57: addi s2, s2, 1
58:

    # FCVT.W.S: the same saturation fix is shared code for both S and D
    # sources (selected by funct7's low bit, not a separate switch), but
    # had no direct single-precision test - a single-precision NaN here
    # confirms that sharing actually covers the .S encoding too, not just
    # the .D one every check above happens to use.
    la t0, flt_nan
    flw f0, 0(t0)
    fcvt.w.s a3, f0, rtz
    li t4, 0x7FFFFFFF
    xor t4, a3, t4
    bnez t4, 59f
    j 60f
59: addi s2, s2, 1
60:

    # mstatus.FS gating: with FS forced to Off, any FP opcode - including
    # this fadd.d, which needs nothing from the (now-stale) register
    # contents to prove the point - must take an illegal-instruction trap
    # (mcause=2) exactly like test J's deliberately-undefined encoding
    # does, through the very same handler, with no FP-specific case needed
    # in it at all. Left Off afterward: nothing past this point in the
    # file uses FP again. `sstatus`, not `mstatus`, for the same reason as
    # the FS-enable above - a `csrc mstatus` here would itself fault from
    # S-mode, and the test would still "pass" (illegal_seen does get set),
    # but for the wrong instruction entirely, never actually reaching the
    # fadd.d this check means to exercise.
    la t0, illegal_seen
    sb x0, 0(t0)
    li t1, 3 << 13
    csrc sstatus, t1
    fadd.d f0, f0, f0
    la t0, illegal_seen
    lbu t2, 0(t0)
    xori t2, t2, 1
    bnez t2, 61f
    j 62f
61: addi s2, s2, 1
62:

    .option pop
    seqz a1, s2
    li a0, 'R'
    jal ra, report

    # --- T: page-crossing load/store across *non-adjacent* physical
    # pages - the exact scenario cpu.c's own `mem_load`/`mem_store` exist
    # for (see README's full writeup): an 8-byte access starting within
    # 7 bytes of a page boundary used to translate only its first byte's
    # address and then read/write the rest contiguously in this
    # emulator's own flat physical-memory array, silently landing
    # wherever that happened to put it rather than wherever the second
    # virtual page's own PTE actually said - invisible unless the two
    # virtual pages genuinely map to non-adjacent physical ones, which no
    # test before this one ever arranged (test M's own aliasing is a
    # single page, never a multi-page crossing). l0_pt's own slots 1 and
    # 2 (free - test M only ever used slot 0) map ALIAS_VA+0x1000 and
    # ALIAS_VA+0x2000 - virtually adjacent by construction - to page_a
    # and page_b, which the 64KB gap between their own declarations
    # guarantees are not.
    la t0, page_a
    li t1, 0xAAAAAAAAAAAAAAAA
    li t2, 0
    li t3, 4096
35: sd t1, 0(t0)
    addi t0, t0, 8
    addi t2, t2, 8
    blt t2, t3, 35b
    la t0, page_b
    li t2, 0
36: sd t1, 0(t0)
    addi t0, t0, 8
    addi t2, t2, 8
    blt t2, t3, 36b

    la t0, l0_pt
    la t4, page_a
    srli t4, t4, 12
    slli t4, t4, 10
    ori t4, t4, PTE_LEAF_RWXAD
    sd t4, 8(t0)                      # l0_pt[1] -> page_a (ALIAS_VA+0x1000)
    la t4, page_b
    srli t4, t4, 12
    slli t4, t4, 10
    ori t4, t4, PTE_LEAF_RWXAD
    sd t4, 16(t0)                     # l0_pt[2] -> page_b (ALIAS_VA+0x2000)
    sfence.vma

    li s2, 0                          # this test's own fail counter

    # Case 1: an 8-byte store at offset 0xFFA - 6 bytes land in page_a,
    # 2 in page_b. Verified by copying 8 bytes from the end of page_a
    # plus 8 bytes from the start of page_b into scratch, one `lbu` at a
    # time, then comparing that 16-byte window against a known-correct
    # table - deliberately never a multi-byte load back through the same
    # crossing path this is testing, so a hypothetical symmetric bug on
    # the read side could never cancel out a real bug on the write side.
    li t6, 0x1122334455667788
    li t0, ALIAS_VA
    li t4, 0x1FFA                     # +0x1000 (page_a's own VA) +0xFFA
    add t0, t0, t4
    sd t6, 0(t0)
    la t0, page_a
    li t4, 0xFF8
    add t0, t0, t4
    la t1, scratch
    li t2, 0
37: lbu t3, 0(t0)
    sb t3, 0(t1)
    addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    li t4, 8
    blt t2, t4, 37b
    la t0, page_b
    li t2, 0
38: lbu t3, 0(t0)
    sb t3, 0(t1)
    addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    blt t2, t4, 38b
    la t0, scratch
    la t1, expected_case1
    li t2, 0
    li t4, 16
39: lbu t3, 0(t0)
    lbu t5, 0(t1)
    bne t3, t5, 40f
    j 41f
40: addi s2, s2, 1
41: addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    blt t2, t4, 39b

    # Reset both pages before case 2, so it starts from the same clean
    # 0xAA pattern rather than case 1's own leftover bytes. t1 itself
    # cannot be trusted to still hold that pattern here - case 1's own
    # verification loop just finished using it as the `expected_case1`
    # pointer, incrementing it 16 times - so it is reloaded explicitly
    # rather than assumed.
    li t1, 0xAAAAAAAAAAAAAAAA
    la t0, page_a
    li t2, 0
    li t3, 4096
42: sd t1, 0(t0)
    addi t0, t0, 8
    addi t2, t2, 8
    blt t2, t3, 42b
    la t0, page_b
    li t2, 0
43: sd t1, 0(t0)
    addi t0, t0, 8
    addi t2, t2, 8
    blt t2, t3, 43b

    # Case 2: the opposite split - offset 0xFFE, 2 bytes in page_a, 6 in
    # page_b.
    li t0, ALIAS_VA
    li t4, 0x1FFE                     # +0x1000 (page_a's own VA) +0xFFE
    add t0, t0, t4
    sd t6, 0(t0)
    la t0, page_a
    li t4, 0xFF8
    add t0, t0, t4
    la t1, scratch
    li t2, 0
44: lbu t3, 0(t0)
    sb t3, 0(t1)
    addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    li t4, 8
    blt t2, t4, 44b
    la t0, page_b
    li t2, 0
45: lbu t3, 0(t0)
    sb t3, 0(t1)
    addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    blt t2, t4, 45b
    la t0, scratch
    la t1, expected_case2
    li t2, 0
    li t4, 16
46: lbu t3, 0(t0)
    lbu t5, 0(t1)
    bne t3, t5, 47f
    j 48f
47: addi s2, s2, 1
48: addi t0, t0, 1
    addi t1, t1, 1
    addi t2, t2, 1
    blt t2, t4, 46b

    # Case 3: the second page is unmapped entirely (l0_pt[2] cleared).
    # The same offset-0xFFA store must now fault - mtval (captured by
    # mtrap_handler above into pagefault_addr_seen, since it is M-only
    # and this test runs in S-mode) landing exactly on page_b's own base
    # address, the first byte of the whole access that was actually on
    # the unmapped page - and, critically, page_a must come back
    # completely untouched: this emulator's own `mem_store` translates
    # and PMP-checks every byte of a crossing access *before* writing
    # any of them, specifically so a fault partway through leaves no
    # partial write behind, unlike real hardware's own instruction-retry
    # model, which this emulator does not implement, making that
    # guarantee load-bearing here rather than cosmetic.
    li t1, 0xAAAAAAAAAAAAAAAA         # reloaded - case 2's own verification
                                       # loop just clobbered t1 the same way
                                       # case 1's did before the fix above
    la t0, page_a
    li t2, 0
    li t3, 4096
49: sd t1, 0(t0)
    addi t0, t0, 8
    addi t2, t2, 8
    blt t2, t3, 49b
    la t0, l0_pt
    sd x0, 16(t0)                     # l0_pt[2]: invalidate page_b's own mapping
    sfence.vma

    la t0, pagefault_seen
    sb x0, 0(t0)
    li t0, ALIAS_VA
    li t4, 0x1FFA                     # +0x1000 (page_a's own VA) +0xFFA
    add t0, t0, t4
    sd t6, 0(t0)                      # must fault - page_b is unmapped now
    la t0, pagefault_seen
    lbu t4, 0(t0)                     # 1 if a page fault actually happened
    la t0, pagefault_addr_seen
    ld t5, 0(t0)
    li t0, ALIAS_VA
    li t1, 0x2000
    add t0, t0, t1                    # page_b's own base - the first
    xor t5, t5, t0                    # unmapped byte the access reached
    seqz t5, t5
    and s3, t4, t5                    # s3 = 1 iff the fault happened AND mtval was right

    # page_a must come back completely untouched - checked into its own
    # flag (s4), independent of s3 above, since both must hold and a
    # single shared register got each successive byte check's own result
    # overwritten by the next one otherwise (a bug caught here in review,
    # not a sign the fix above has the same issue - this loop was simply
    # written wrong the first time, clobbering the loop bound with the
    # last byte compared instead of a separate counter).
    la t0, page_a
    li t2, 0
    li t4, 4096
    li s4, 1                          # assume unchanged until proven otherwise
53: lbu t3, 0(t0)
    xori t3, t3, 0xAA
    beqz t3, 54f
    li s4, 0
54: addi t0, t0, 1
    addi t2, t2, 1
    blt t2, t4, 53b

    and s3, s3, s4
    xori s3, s3, 1
    add s2, s2, s3

    seqz a1, s2
    li a0, 'T'
    jal ra, report

    la a0, msg_done
    jal ra, puts

    li t0, 0x5555
    li t1, TESTDEV
    sw t0, 0(t1)
    j halt_loop

# Runs in S-mode after the MRET above. Deliberately uses only instructions
# and physical-memory MMIO that need no privilege beyond S (no paging exists
# yet in Phase 3's sense, so S-mode addresses physical memory exactly like
# M-mode does - the point under test here is the trap delegation, not memory
# protection).
s_mode_code:
    ecall                            # delegated: traps to stvec, not mtvec
    j after_s_mode                   # strap_handler's SRET lands back here

# --- M-mode trap vector: ECALL-from-M, illegal instruction, and EBREAK all
# funnel through mtvec since none of them are delegated. Each case records
# a flag byte the test body already cleared, then skips past the faulting
# instruction and returns. ---------------------------------------------------
#
# An interrupt (unlike every exception above) can land between any two
# instructions of whatever the test was doing when it fired, so both
# handlers below save and restore every register they touch - the same
# discipline a real OS trap handler follows, and no longer optional now
# that one of them (strap_handler) must itself call back into the other.
mtrap_handler:
    addi sp, sp, -64
    sd ra, 0(sp)
    sd t0, 8(sp)
    sd t1, 16(sp)
    sd t2, 24(sp)
    sd t3, 32(sp)
    sd a0, 40(sp)
    sd a1, 48(sp)
    sd a7, 56(sp)

    csrr t0, mcause
    li t1, 11
    beq t0, t1, 1f                  # ECALL from M-mode
    li t1, 2
    beq t0, t1, 2f                  # illegal instruction
    li t1, 3
    beq t0, t1, 3f                  # breakpoint
    li t1, 13
    beq t0, t1, 4f                  # load page fault
    li t1, 15
    beq t0, t1, 4f                  # store/AMO page fault - test T's own
                                     # page-crossing store needs this one;
                                     # nothing before it ever faulted a
                                     # store, only a load (test N)
    li t1, 9
    beq t0, t1, 6f                  # ECALL from S-mode: a real SBI call
    li t1, 0x8000000000000007       # machine timer interrupt
    beq t0, t1, 5f
    j mtrap_unexpected
1:  la t2, ecall_m_seen
    j mtrap_mark
2:  la t2, illegal_seen
    # Also capture mstatus.MPP (the privilege this trap says it came from),
    # for test S below to check directly - t0/t1 are free to clobber here,
    # the dispatch above is done with them and both get restored from the
    # stack regardless of what happens to them in this handler body.
    csrr t0, mstatus
    srli t0, t0, 11
    andi t0, t0, 3
    la t1, illegal_mpp_seen
    sb t0, 0(t1)
    j mtrap_mark
3:  la t2, ebreak_seen
    j mtrap_mark
4:  la t2, pagefault_seen
    # Also capture mtval (the faulting address) into guest memory, for
    # the page-crossing store test below to check directly - mtval itself
    # is M-only (0x343, no S-mode alias the way mstatus/mie have), so
    # from S-mode (everywhere past test L) there is no other way to read
    # back what this specific fault's own mtval actually held.
    csrr t0, mtval
    la t1, pagefault_addr_seen
    sd t0, 0(t1)
mtrap_mark:
    li t3, 1
    sb t3, 0(t2)
    j mtrap_return_exception
6:  # SBI call. Only legacy EID 0 ("set timer") is implemented - a0 holds
    # the new mtimecmp value. Real firmware offers far more; this is
    # enough for our own test's timer servicing, which is the point: this
    # call exists on real hardware specifically because only M-mode can
    # clear STIP, confirmed the hard way (see strap_handler below).
    bnez a7, mtrap_unexpected
    li t2, CLINT_MTIMECMP
    sd a0, 0(t2)
    li t1, 1 << 5
    csrc mip, t1
    j mtrap_return_exception
5:  # Machine timer interrupt. Disarm it, inject a pending supervisor
    # timer interrupt via mip directly (M-mode may write any bit of it),
    # delegate STI so future occurrences reach S without M's help, and -
    # the first time only, but harmless to repeat - stop delegating
    # ECALL-from-S: a real SBI call must always reach M, never S.
    li t1, -1
    li t2, CLINT_MTIMECMP
    sd t1, 0(t2)
    li t1, 1 << 5
    csrs mip, t1
    csrs mideleg, t1
    li t1, 1 << 9
    csrc medeleg, t1
    la t2, mti_in_m_seen
    li t3, 1
    sb t3, 0(t2)
    j mtrap_return_interrupt
mtrap_return_exception:
    csrr t0, mepc                   # exceptions must skip the faulting
    addi t0, t0, 4                  # instruction; interrupts must not
    csrw mepc, t0
mtrap_return_interrupt:
    ld ra, 0(sp)
    ld t0, 8(sp)
    ld t1, 16(sp)
    ld t2, 24(sp)
    ld t3, 32(sp)
    ld a0, 40(sp)
    ld a1, 48(sp)
    ld a7, 56(sp)
    addi sp, sp, 64
    mret
mtrap_unexpected:
    j mtrap_unexpected

# --- S-mode trap vector: a delegated ECALL-from-S (only before the first
# timer interrupt rewires that delegation) or a delegated STI. -------------
strap_handler:
    addi sp, sp, -64
    sd ra, 0(sp)
    sd t0, 8(sp)
    sd t1, 16(sp)
    sd t2, 24(sp)
    sd t3, 32(sp)
    sd a0, 40(sp)
    sd a1, 48(sp)
    sd a7, 56(sp)

    csrr t0, scause
    li t1, 9
    beq t0, t1, 1f                   # delegated ECALL from S-mode
    li t1, 0x8000000000000005        # supervisor timer interrupt
    beq t0, t1, 5f
    j strap_unexpected
1:  la t2, ecall_s_seen
    li t3, 1
    sb t3, 0(t2)
    j strap_return_exception
5:  # Supervisor timer interrupt. STIP is read-only through sip - confirmed
    # against real hardware, not assumed - so S-mode cannot clear it by
    # itself; ask M-mode to do it via the legacy SBI "set timer" call,
    # which reprograms CLINT and clears STIP as one operation. This nests
    # into mtrap_handler and back before we reach our own sret below; its
    # own save/restore keeps that safe.
    la t2, stip_seen
    li t3, 1
    sb t3, 0(t2)
    li a7, 0
    li a0, -1                        # rearm far in the future: done with timers
    ecall
    j strap_return_interrupt
strap_return_exception:
    csrr t0, sepc
    addi t0, t0, 4
    csrw sepc, t0
strap_return_interrupt:
    ld ra, 0(sp)
    ld t0, 8(sp)
    ld t1, 16(sp)
    ld t2, 24(sp)
    ld t3, 32(sp)
    ld a0, 40(sp)
    ld a1, 48(sp)
    ld a7, 56(sp)
    addi sp, sp, 64
    sret
strap_unexpected:
    j strap_unexpected

add_one:
    addi a0, a2, 1
    ret

# Exercises c.jalr (the caller's plain jal reaches here) and c.jr (this
# function's own return) together - both are the compressed indirect-jump
# forms test Q's own c.j/c.beqz/c.bnez exercise less directly.
q_call_target:
    .option push
    .option arch, +c
    li a0, 42
    c.jr ra
    .option pop

# report: a0 = test id char, a1 = 1 (pass) or 0 (fail). Non-leaf: calls puts,
# so it must save/restore ra itself around that call.
report:
    addi sp, sp, -16
    sd ra, 8(sp)
    sd a0, 0(sp)

    beqz a1, 1f
    j 2f
1:  addi s0, s0, 1
2:
    li t0, UART_BASE
    lb t1, 0(sp)
    sb t1, 0(t0)
    li t1, ' '
    sb t1, 0(t0)

    beqz a1, 3f
    la a0, msg_ok
    j 4f
3:  la a0, msg_fail
4:  jal ra, puts

    ld ra, 8(sp)
    addi sp, sp, 16
    ret

# puts: a0 = pointer to a NUL-terminated string. Leaf routine (calls
# nothing), so it can use ra directly without saving it.
puts:
    li t0, UART_BASE
1:
    lbu t1, 0(a0)
    beqz t1, 2f
    sb t1, 0(t0)
    addi a0, a0, 1
    j 1b
2:
    ret

halt_loop:
    j halt_loop

.section .rodata
msg_ok:   .asciz "ok\n"
msg_fail: .asciz "FAIL\n"
msg_done: .asciz "selftest complete\n"

# IEEE 754 double-precision bit patterns for test R - computed by hand,
# not by the assembler's own `.double` (which this assembler build does
# not support here), the same reason every other test constant in this
# file is a plain integer literal rather than something derived.
.p2align 3
dbl_1_0:      .dword 0x3FF0000000000000   # 1.0
dbl_1_5:      .dword 0x3FF8000000000000   # 1.5
dbl_2_0:      .dword 0x4000000000000000   # 2.0
dbl_2_5:      .dword 0x4004000000000000   # 2.5
dbl_3_0:      .dword 0x4008000000000000   # 3.0
dbl_4_0:      .dword 0x4010000000000000   # 4.0
dbl_5_0:      .dword 0x4014000000000000   # 5.0
dbl_6_0:      .dword 0x4018000000000000   # 6.0
dbl_7_0:      .dword 0x401C000000000000   # 7.0
dbl_10_0:     .dword 0x4024000000000000   # 10.0
dbl_16_0:     .dword 0x4030000000000000   # 16.0
dbl_42_0:     .dword 0x4045000000000000   # 42.0
dbl_pos_inf:  .dword 0x7FF0000000000000   # +Infinity
dbl_neg_3_0:  .dword 0xC008000000000000   # -3.0
dbl_neg_5_0:  .dword 0xC014000000000000   # -5.0
dbl_0_0:        .dword 0x0000000000000000 # +0.0
dbl_neg_0_0:    .dword 0x8000000000000000 # -0.0
dbl_neg_inf:    .dword 0xFFF0000000000000 # -Infinity
dbl_nan:        .dword 0x7FF8000000000000 # a quiet NaN, canonical
dbl_nan_payload: .dword 0x7FF0000000001234 # a NaN with a nonzero, non-canonical payload
dbl_int64_max:  .dword 0x7FFFFFFFFFFFFFFF # INT64_MAX
dbl_uint32_max: .dword 0xFFFFFFFFFFFFFFFF # UINT32_MAX (0xFFFFFFFF), sign-extended per spec
flt_1_5:      .word  0x3FC00000           # 1.5f, single precision
flt_nan:      .word  0x7FC00000           # a quiet NaN, single precision

# Test T's own expected 16-byte windows: page_a's last 8 bytes
# (0xFF8-0xFFF) followed by page_b's first 8 bytes (0x0-0x7), after an
# 8-byte store of 0x1122334455667788 (little-endian bytes 88 77 66 55 44
# 33 22 11) at the one offset each case uses, against page_a/page_b
# otherwise left at the 0xAA fill pattern test T's own setup writes.
expected_case1: .byte 0xAA, 0xAA, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA
expected_case2: .byte 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0xAA, 0x88, 0x77, 0x66, 0x55, 0x44, 0x33, 0x22, 0x11, 0xAA, 0xAA

.section .bss
.align 4
scratch:      .space 16
ecall_m_seen:  .space 1
illegal_seen:  .space 1
illegal_mpp_seen: .space 1
ebreak_seen:   .space 1
ecall_s_seen:  .space 1
pagefault_seen: .space 1
mti_in_m_seen:  .space 1
stip_seen:      .space 1
.align 3
pagefault_addr_seen: .space 8

# Sv39 page tables: each must be physically page-aligned, since a PTE's PPN
# field only encodes a full page number (the low 12 address bits are
# implicit zero).
.p2align 12
root_pt:   .space 4096
l1_pt:     .space 4096
l0_pt:     .space 4096
test_page: .space 4096

# Two physical pages for the page-crossing load/store test below,
# deliberately separated by a large gap so they can never land on
# adjacent (or even nearby) physical pages regardless of how the linker
# happens to lay out .bss - the whole point is mapping two *virtually*
# adjacent pages to two *non*-adjacent physical ones.
.p2align 12
page_a: .space 4096
.space 65536
.p2align 12
page_b: .space 4096

.space 4096
stack_top:

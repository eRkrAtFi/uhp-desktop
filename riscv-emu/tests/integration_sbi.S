/* Integration test: exercises the real boot/firmware.S binary's SBI call
 * dispatch end to end, as opposed to tests/selftest.S's Phase 2-4 cases,
 * which hand-roll their own minimal M-mode trap setup inline and never
 * load or execute the actual firmware.S image at all. This is the
 * difference between "the CPU's ECALL/trap mechanics are correct" (already
 * covered) and "firmware.S's own SBI dispatch table does the right thing
 * for a real caller" (not covered anywhere else) - exactly the gap between
 * unit and integration testing.
 *
 * firmware.S hands off to this code in S-mode (a0=hartid, a1=dtb address -
 * unused here, no DTB needed for pure SBI calls). Run the normal way
 * Phase 5 runs anything on top of firmware.S: --load firmware.bin at
 * RAM_BASE, --load this binary elsewhere, --reg t3 <its address>.
 *
 * Verified the same way as every other test here: make integration-compare
 * runs this exact binary under riscv-emu+our own firmware.S AND under real
 * qemu-system-riscv64 -M virt with real OpenSBI (-bios default), and diffs
 * the two console outputs - the same standardized SBI behavior (base
 * extension probing, HSM hart status, legacy putchar) should look
 * identical regardless of which firmware answers the calls.
 */
.option norvc
.section .text
.global _start
_start:
    la sp, stack_top

    /* --- SBI base extension (EID 0x10): probe itself --- */
    li a7, 0x10
    li a6, 3            /* probe_extension */
    li a0, 0x10
    ecall
    bnez a0, fail1      /* a0 = SBI_SUCCESS (0) */
    li t0, 1
    bne a1, t0, fail1   /* a1 = 1: base extension is present (of course) */

    /* --- probe the legacy extension range (EID 1, putchar) --- */
    li a7, 0x10
    li a6, 3
    li a0, 1
    ecall
    bnez a0, fail2
    li t0, 1
    bne a1, t0, fail2

    /* --- HSM (EID "HSM"): hart 0 must already be running --- */
    li a7, 0x48534D
    li a6, 2            /* hart_get_status */
    li a0, 0
    ecall
    bnez a0, fail3      /* SBI_SUCCESS */
    bnez a1, fail3      /* HSM_STATE_STARTED == 0 */

    /* --- legacy putchar (EID 1): print "OK" directly through SBI, not
     * through a raw UART write - this is the actual thing under test. --- */
    li a7, 1
    li a0, 'O'
    ecall
    li a7, 1
    li a0, 'K'
    ecall
    li a7, 1
    li a0, 10
    ecall

    li t1, 0x5555
    li t2, 0x00100000
    sw t1, 0(t2)
1:  j 1b

fail1: li s0, 1; j failcommon
fail2: li s0, 2; j failcommon
fail3: li s0, 3; j failcommon
failcommon:
    /* print "FAIL" + the code via legacy putchar too, so a human reading
     * either side's console output can tell which check failed without
     * needing --trace. */
    li a7, 1
    li a0, 'F'; ecall
    li a0, 'A'; ecall
    li a0, 'I'; ecall
    li a0, 'L'; ecall
    li a0, ' '; ecall
    addi a0, s0, '0'
    ecall
    li a0, 10
    ecall

    slli s0, s0, 16
    li t1, 0x3333
    or s0, s0, t1
    li t2, 0x00100000
    sw s0, 0(t2)
1:  j 1b

.section .bss
.space 4096
stack_top:

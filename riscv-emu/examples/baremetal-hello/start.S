/* Minimal startup for hello.c: set sp, call into C. Same pattern as
 * tests/virtio_start.S - loops forever if main ever returns, though it
 * should not (main ends via the test-finisher device, like every other
 * guest program in this repo). */
.option norvc
.section .text
.global _start
_start:
    la sp, stack_top
    call main
1:  j 1b

.section .bss
.space 8192
stack_top:

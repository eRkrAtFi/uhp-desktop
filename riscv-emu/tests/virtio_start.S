/* Minimal startup for virtio_test.c: set sp, call into C, loop forever if
 * cmain ever returns (it should not - every path through it ends via the
 * test-finisher device, same as every other test here). */
.option norvc
.section .text
.global _start
_start:
    la sp, stack_top
    call cmain
1:  j 1b

.section .bss
.space 8192
stack_top:

# Minimal S-mode test program for firmware.S's SBI legacy getchar/putchar:
# loops forever, polling getchar (EID 2) and echoing any byte back via
# putchar (EID 1). Not part of selftest.S's own M-mode-only suite - this
# needs a real S-mode guest making real ECALLs, which is exactly what
# firmware.S's legacy SBI handlers are for.
.section .text
.global _start
_start:
loop:
    li a7, 2          # legacy getchar
    ecall
    bltz a0, loop     # -1: no input yet, keep polling
    li a7, 1          # legacy putchar
    ecall
    j loop

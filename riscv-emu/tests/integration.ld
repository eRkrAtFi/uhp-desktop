/* Links tests/integration_sbi.S (and any other S-mode-handoff test program
 * that needs to run alongside firmware.S rather than instead of it) at
 * RAM_BASE + 0x200000 - clear of boot/firmware.bin's own ~1KB footprint at
 * RAM_BASE, and (the address that actually mattered in practice) clear of
 * real OpenSBI's fw_dynamic image too, which is much bigger than our own
 * firmware.S and runs up to roughly RAM_BASE + 0x44000 - confirmed by
 * QEMU refusing to start at 0x80001000 with "Some ROM regions are
 * overlapping" the first time this was tried. 0x200000 is also the same
 * offset boot.sh places the real Linux kernel Image at, so this one
 * address convention covers "a real payload, running under a real
 * firmware, differentially against real QEMU" uniformly. Unlike
 * tests/link.ld (0x80000000, where the Phase 1-4 selftest *is* the only
 * thing in RAM, with no firmware underneath it), anything linked here is
 * meant to be --load-ed next to boot/firmware.bin, not instead of it. */
ENTRY(_start)

SECTIONS
{
    . = 0x80200000;
    .text   : { *(.text*) }
    .rodata : { *(.rodata*) }
    .data   : { *(.data*) }
    .bss    : { *(.bss*) }
}

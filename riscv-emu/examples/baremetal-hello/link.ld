/* Places _start at exactly 0x80000000 - see tests/link.ld's own comment
 * for why (that reasoning applied to a flat-binary loader that no
 * longer exists for this file specifically: --elf parses real PT_LOAD
 * segments, so header overhead would not actually break anything here -
 * kept anyway for consistency with every other guest program in this
 * repo, and so a bare `objcopy -O binary` dump of this one also starts
 * with code rather than header bytes, same as the others). */
ENTRY(_start)

SECTIONS
{
    . = 0x80000000;
    .text   : { *(.text*) }
    .rodata : { *(.rodata*) }
    .data   : { *(.data*) }
    /* The RISC-V calling convention requires sp 16-byte aligned at a
     * call boundary - start.S's own "la sp, stack_top" is only as
     * aligned as stack_top's own address, which nothing forced before
     * this: .bss's own start (and so stack_top, exactly 8192 - itself a
     * multiple of 16 - bytes past it) landed whatever odd byte .data
     * happened to end on (confirmed directly via lldb: sp =
     * 0x8000225f, nowhere near 16-aligned). */
    . = ALIGN(16);
    .bss    : { *(.bss*) }
}

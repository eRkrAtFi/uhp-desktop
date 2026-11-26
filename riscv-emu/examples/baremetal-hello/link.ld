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
    .bss    : { *(.bss*) }
}

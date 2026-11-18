/* Places _start at exactly 0x80000000 with nothing before it - no ELF
 * header, no program header table occupying the start of the load image.
 * Without this, lld's default executable layout reserves header space as
 * part of the first loadable segment, so _start ends up hundreds of bytes
 * into the image; a raw `objcopy -O binary` dump would then start with
 * header bytes instead of code, which our flat-binary loader (which always
 * begins execution at RAM_BASE) would misinterpret as instructions.
 * QEMU's own ELF loader does not have this problem - it respects e_entry
 * regardless of layout - so this script matters only for keeping our two
 * outputs (the ELF for QEMU, the raw .bin for us) in agreement.
 */
ENTRY(_start)

SECTIONS
{
    . = 0x80000000;
    .text   : { *(.text*) }
    .rodata : { *(.rodata*) }
    .data   : { *(.data*) }
    .bss    : { *(.bss*) }
}

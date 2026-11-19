# Buildroot's own /etc/profile (system/skeleton/etc/profile) unconditionally
# overwrites PS1 to '# ' (running as uid 0, which auto-login makes us) in
# its own `if [ "$PS1" ]; then ... fi` block, BEFORE it sources every
# /etc/profile.d/*.sh in glob order - confirmed directly against
# Buildroot 2026.08.2's own skeleton package. Setting PS1 any earlier
# (e.g. in riscv-emu-shell, before exec'ing a login shell) is silently
# lost to that overwrite. "zz-" makes this the last profile.d script to
# run in the common case (plain alphabetical glob order), so this is the
# final word on PS1 for this image.
export PS1='riscv-emu# '

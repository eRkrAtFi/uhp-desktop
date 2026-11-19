# Shared source of truth for the two text markers boot_test.sh/
# interactive_test.sh grep the guest's own console output for - previously
# each literal was duplicated across both scripts (plus six more copies in
# QemuManager.SelfTest/Program.cs), so swapping to a new kernel/initramfs
# image meant hunting down every copy individually. Now there is exactly
# one shell-side place to edit (this file) and one C#-side place
# (QemuManager.SelfTest/Program.cs's own GuestPrompt constant).
#
# Updated from a real boot log of the Buildroot-built image (riscv-emu
# and real qemu-system-riscv64 both reach this same prompt). getty's own
# -n/-l bypass (see rootfs-overlay/etc/inittab) means /bin/login never
# runs, so /etc/issue is never actually printed during boot the way a
# normal login prompt would show it - there is no separate "banner" line
# distinct from the prompt itself any more, so both variables are the
# same text. GUEST_BANNER is kept (rather than having boot_test.sh
# switch to GUEST_PROMPT directly) only so a future image that DOES have
# a separate boot banner has somewhere to put it without another round
# of grep/call-site changes.
GUEST_BANNER='riscv-emu#'
GUEST_PROMPT='riscv-emu#'

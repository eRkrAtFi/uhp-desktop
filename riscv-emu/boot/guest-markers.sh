# Shared source of truth for the two text markers boot_test.sh/
# interactive_test.sh grep the guest's own console output for - previously
# each literal was duplicated across both scripts (plus six more copies in
# QemuManager.SelfTest/Program.cs), so swapping to a new kernel/initramfs
# image meant hunting down every copy individually. Now there is exactly
# one shell-side place to edit (this file) and one C#-side place
# (QemuManager.SelfTest/Program.cs's own GuestPrompt constant).
#
# Still the OLD (undocumented-provenance) image's own values as of this
# commit - do NOT change these ahead of actually booting the new
# Buildroot-built image. See THIRD_PARTY.md / the Buildroot guest-images
# plan for why, and update both this file and GuestPrompt together, from
# the real boot log, once that image is verified to boot.
GUEST_BANNER="Welcome to you Linux RISC-V"
GUEST_PROMPT='~ #'

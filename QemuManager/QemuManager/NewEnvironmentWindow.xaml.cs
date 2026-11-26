using System.Windows;

namespace QemuManager;

/// <summary>
/// Template picker for a new Environment - was NewVmKindWindow (just
/// "QEMU" vs "riscv-emu"); renamed and relabeled per vision/VISION.md's
/// own "New Environment" templates. Only "Operating System" (QEMU) and
/// "Kernel / Bootloader" (riscv-emu) are offered - the two this app can
/// actually run today; see vision/NEXT.md for why "Bare-metal / Embedded"
/// is deliberately not here yet, and the vision's own full list for the
/// others ("Firmware", "Custom") this does not attempt at all.
/// </summary>
public partial class NewEnvironmentWindow : Window
{
    /// <summary>"Qemu" or "RiscvEmu" (matches IEngine.Id exactly), set
    /// only when the dialog closes with DialogResult true.</summary>
    public string SelectedEngineId { get; private set; } = "Qemu";

    public NewEnvironmentWindow()
    {
        InitializeComponent();
    }

    private void OperatingSystemButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEngineId = "Qemu";
        DialogResult = true;
    }

    private void KernelBootloaderButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEngineId = "RiscvEmu";
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

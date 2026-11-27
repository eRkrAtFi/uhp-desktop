using System.Windows;

namespace QemuManager;

/// <summary>
/// Template picker for a new Environment - was NewVmKindWindow (just
/// "QEMU" vs "riscv-emu"); renamed and relabeled per vision/VISION.md's
/// own "New Environment" templates. "Operating System" (QEMU),
/// "Kernel / Bootloader" (riscv-emu), and "Bare-metal / Embedded"
/// (riscv-emu, with Workload.ElfPath/Debug.GdbEnabled pre-set - roadmap
/// item 3) are offered; the vision's own full list has others
/// ("Firmware", "Custom") this does not attempt at all.
/// </summary>
public partial class NewEnvironmentWindow : Window
{
    /// <summary>"Qemu" or "RiscvEmu" (matches IEngine.Id exactly), set
    /// only when the dialog closes with DialogResult true.</summary>
    public string SelectedEngineId { get; private set; } = "Qemu";

    /// <summary>True only for the "Bare-metal / Embedded" template -
    /// MainWindow's own NewButton_Click uses this to pre-set
    /// Workload.ElfPath-related defaults (Debug.GdbEnabled,
    /// Debug.EnterSends = "LF") that the other two templates should not
    /// get.</summary>
    public bool IsBareMetal { get; private set; }

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

    private void BareMetalButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedEngineId = "RiscvEmu";
        IsBareMetal = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

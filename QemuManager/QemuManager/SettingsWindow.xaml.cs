using System.Windows;
using QemuManager.Services;

namespace QemuManager;

public partial class SettingsWindow : Window
{
    public string QemuDir { get; private set; }
    public string RiscvEmuDir { get; private set; }

    public SettingsWindow(string currentQemuDir, string currentRiscvEmuDir)
    {
        InitializeComponent();
        QemuDir = currentQemuDir;
        RiscvEmuDir = currentRiscvEmuDir;
        QemuDirBox.Text = currentQemuDir;
        RiscvEmuDirBox.Text = currentRiscvEmuDir;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the folder containing qemu-system-*.exe",
            SelectedPath = QemuDirBox.Text,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            QemuDirBox.Text = dialog.SelectedPath;
    }

    private void RiscvEmuBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the riscv-emu folder (contains riscv-emu.exe and boot\\firmware.bin)",
            SelectedPath = RiscvEmuDirBox.Text,
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            RiscvEmuDirBox.Text = dialog.SelectedPath;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var path = QemuDirBox.Text.Trim();
        var riscvPath = RiscvEmuDirBox.Text.Trim();
        var warnings = new System.Collections.Generic.List<string>();
        if (!QemuLocator.LooksLikeQemuDir(path))
            warnings.Add("No qemu-system-*.exe found there; QEMU VMs will fail to start.");
        if (!string.IsNullOrWhiteSpace(riscvPath) && !RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvPath))
            warnings.Add("riscv-emu.exe or boot\\firmware.bin not found there; riscv-emu VMs will fail to start.");
        ValidationText.Text = warnings.Count > 0 ? "Saving anyway: " + string.Join(" ", warnings) : "";

        QemuDir = path;
        RiscvEmuDir = riscvPath;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

using System.Windows;
using QemuManager.Services;

namespace QemuManager;

public partial class SettingsWindow : Window
{
    public string QemuDir { get; private set; }
    public string RiscvEmuDir { get; private set; }
    public int RiscvShutdownTimeoutSeconds { get; private set; }

    public SettingsWindow(string currentQemuDir, string currentRiscvEmuDir, int currentRiscvShutdownTimeoutSeconds)
    {
        InitializeComponent();
        QemuDir = currentQemuDir;
        RiscvEmuDir = currentRiscvEmuDir;
        RiscvShutdownTimeoutSeconds = currentRiscvShutdownTimeoutSeconds;
        QemuDirBox.Text = currentQemuDir;
        RiscvEmuDirBox.Text = currentRiscvEmuDir;
        RiscvShutdownTimeoutBox.Text = currentRiscvShutdownTimeoutSeconds.ToString();
        VersionText.Text = AppVersion.DisplayString;
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

        // Any non-positive value would make ShutdownAsync give up before
        // a genuinely-shutting-down guest had any real chance to - fall
        // back to the previous value rather than silently saving
        // something that defeats the whole point of this field, same as
        // VmService.ShutdownAsync's own Math.Max(1, ...) floor already
        // does defensively for a value that somehow got here anyway.
        var timeoutText = RiscvShutdownTimeoutBox.Text.Trim();
        if (int.TryParse(timeoutText, out var timeoutSeconds) && timeoutSeconds > 0)
        {
            RiscvShutdownTimeoutSeconds = timeoutSeconds;
        }
        else
        {
            warnings.Add($"\"{timeoutText}\" is not a positive whole number of seconds; keeping the previous Shut Down timeout ({RiscvShutdownTimeoutSeconds}s).");
        }

        ValidationText.Text = warnings.Count > 0 ? "Saving anyway: " + string.Join(" ", warnings) : "";

        QemuDir = path;
        RiscvEmuDir = riscvPath;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

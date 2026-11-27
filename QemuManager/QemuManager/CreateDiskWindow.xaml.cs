using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QemuManager.Services;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace QemuManager;

public partial class CreateDiskWindow : Window
{
    private readonly string _qemuDir;
    private readonly IVmService _vmService;

    /// <summary>Path of the disk image created, set only after a successful Create.</summary>
    public string CreatedPath { get; private set; } = "";

    /// <summary>defaultFormat picks which ComboBoxItem starts selected -
    /// "raw" when the caller already knows only riscv-emu can ever use
    /// this disk (that engine has no other option - see
    /// RiscvEmuProcessManager's own Workload.DiskPath validation),
    /// "qcow2" otherwise (PR #7 feedback). Either can still be changed
    /// here before Create.</summary>
    public CreateDiskWindow(string qemuDir, string suggestedPath, IVmService vmService, string defaultFormat = "qcow2")
    {
        InitializeComponent();
        _qemuDir = qemuDir;
        _vmService = vmService;
        PathBox.Text = suggestedPath;
        FormatCombo.SelectedIndex = string.Equals(defaultFormat, "raw", System.StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "Disk image (*.img;*.raw;*.qcow2)|*.img;*.raw;*.qcow2|All files (*.*)|*.*",
            FileName = PathBox.Text,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() == true) PathBox.Text = dialog.FileName;
    }

    private async void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            ValidationText.Text = "Choose a file path.";
            return;
        }
        if (!double.TryParse(SizeBox.Text.Trim(), out var size) || size <= 0)
        {
            ValidationText.Text = "Size must be a positive number.";
            return;
        }
        var unit = (SizeUnitCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "GB";
        var sizeBytes = (long)(size * (unit == "MB" ? 1024.0 * 1024 : 1024.0 * 1024 * 1024));
        var format = (FormatCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "qcow2";

        // Never silently overwritten (a real bug: qemu-img create has no
        // such guard of its own at all - two different disks created
        // with the same default suggested path, the second truncating
        // the first with no warning). Never even offered as an option
        // for a disk some Environment actually uses; for an unused one,
        // an explicit Yes/No takes the place of "just pick a different
        // name" if that is genuinely what the person wants.
        var overwrite = false;
        if (File.Exists(path))
        {
            var usedBy = _vmService.GetEnvironmentsUsingDisk(path);
            if (usedBy.Count > 0)
            {
                ValidationText.Text = $"{path} already exists and is used by {string.Join(", ", usedBy)} - choose a different name.";
                return;
            }
            var confirm = MessageBox.Show(this,
                $"{path} already exists and is not used by any Environment.\n\nOverwrite it?",
                "File already exists", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;
            overwrite = true;
        }

        CreateButton.IsEnabled = false;
        ValidationText.Text = "Creating...";
        var message = await DiskImageService.CreateAsync(_qemuDir, path, sizeBytes, format, overwrite);
        CreateButton.IsEnabled = true;

        var success = message.StartsWith("Created");
        _vmService.RecordTask("Create Disk", path, success, message);
        if (success)
        {
            // Every disk created via Create disk is recorded regardless
            // of where it was saved (PR #7 feedback) - here, not in each
            // caller, so every entry point that opens this dialog gets
            // it for free.
            await _vmService.RegisterDiskAsync(path);
            CreatedPath = path;
            DialogResult = true;
        }
        else
        {
            ValidationText.Text = message;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

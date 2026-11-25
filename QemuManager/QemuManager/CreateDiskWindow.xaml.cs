using System.Windows;
using Microsoft.Win32;
using QemuManager.Services;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace QemuManager;

public partial class CreateDiskWindow : Window
{
    private readonly string _qemuDir;
    private readonly IVmService _vmService;

    /// <summary>Path of the disk image created, set only after a successful Create.</summary>
    public string CreatedPath { get; private set; } = "";

    public CreateDiskWindow(string qemuDir, string suggestedPath, IVmService vmService)
    {
        InitializeComponent();
        _qemuDir = qemuDir;
        _vmService = vmService;
        PathBox.Text = suggestedPath;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "QEMU disk image (*.qcow2)|*.qcow2",
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
        if (!int.TryParse(SizeBox.Text.Trim(), out var sizeGB) || sizeGB <= 0)
        {
            ValidationText.Text = "Size must be a positive whole number of GB.";
            return;
        }

        CreateButton.IsEnabled = false;
        ValidationText.Text = "Creating...";
        var message = await DiskImageService.CreateQcow2Async(_qemuDir, path, sizeGB);
        CreateButton.IsEnabled = true;

        var success = message.StartsWith("Created");
        _vmService.RecordTask("Create Disk", path, success, message);
        if (success)
        {
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

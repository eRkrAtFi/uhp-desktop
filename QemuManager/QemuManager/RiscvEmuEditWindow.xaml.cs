using System.Windows;
using QemuManager.Models;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace QemuManager;

public partial class RiscvEmuEditWindow : Window
{
    private readonly VmConfig _vm;

    public RiscvEmuEditWindow(VmConfig vm)
    {
        InitializeComponent();
        _vm = vm;
        LoadFromConfig();
    }

    private void LoadFromConfig()
    {
        NameBox.Text = _vm.Name;
        KernelBox.Text = _vm.KernelPath;
        InitrdBox.Text = _vm.InitrdPath;
        DiskBox.Text = _vm.DiskPath;
        // 256 is this app's own historical default, not riscv-emu.exe's -
        // see src/emu.h's MEM_SIZE for what an empty --mem actually falls
        // back to - kept here only so a VM saved before this field existed
        // shows a sensible starting value rather than 0.
        MemoryBox.Text = (_vm.MemoryMB > 0 ? _vm.MemoryMB : 256).ToString();
        NetworkCheckBox.IsChecked = _vm.NetworkMode == "user";
        ExtraArgsBox.Text = _vm.ExtraArgs;
        ShutdownCommandBox.Text = _vm.ShutdownCommand;
    }

    private void KernelBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Linux Image (Image;*.img;*.elf)|Image;*.img;*.elf|All files (*.*)|*.*",
            FileName = KernelBox.Text,
        };
        if (dialog.ShowDialog() == true) KernelBox.Text = dialog.FileName;
    }

    private void InitrdBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Initramfs (*.cpio.gz;*.cpio;*.img)|*.cpio.gz;*.cpio;*.img|All files (*.*)|*.*",
            FileName = InitrdBox.Text,
        };
        if (dialog.ShowDialog() == true) InitrdBox.Text = dialog.FileName;
    }

    private void InitrdClearButton_Click(object sender, RoutedEventArgs e) => InitrdBox.Text = "";

    private void DiskBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Disk image (*.img;*.raw)|*.img;*.raw|All files (*.*)|*.*",
            FileName = DiskBox.Text,
        };
        if (dialog.ShowDialog() == true) DiskBox.Text = dialog.FileName;
    }

    private void DiskClearButton_Click(object sender, RoutedEventArgs e) => DiskBox.Text = "";

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationText.Text = "Name is required."; return; }
        var kernel = KernelBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(kernel)) { ValidationText.Text = "A kernel Image is required."; return; }
        if (!int.TryParse(MemoryBox.Text.Trim(), out var memoryMB) || memoryMB <= 0)
        {
            ValidationText.Text = "Memory must be a positive number of MB.";
            return;
        }
        var shutdownCommand = ShutdownCommandBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(shutdownCommand))
        {
            ValidationText.Text = "Shutdown command is required (Force Stop is always still available as a fallback).";
            return;
        }

        _vm.Name = name;
        _vm.Kind = "RiscvEmu";
        _vm.KernelPath = kernel;
        _vm.InitrdPath = InitrdBox.Text.Trim();
        _vm.DiskPath = DiskBox.Text.Trim();
        _vm.MemoryMB = memoryMB;
        _vm.NetworkMode = NetworkCheckBox.IsChecked == true ? "user" : "none";
        _vm.ExtraArgs = ExtraArgsBox.Text.Trim();
        _vm.ShutdownCommand = shutdownCommand;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

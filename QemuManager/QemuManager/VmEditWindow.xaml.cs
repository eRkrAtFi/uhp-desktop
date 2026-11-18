using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using QemuManager.Models;
using QemuManager.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace QemuManager;

public partial class VmEditWindow : Window
{
    private readonly VmConfig _vm;
    private readonly string _qemuDir;

    /// <summary>Known architectures with no usable default QEMU machine
    /// type/firmware (see ARCHITECTURE_AUDIT.md's recommendation #3) - a
    /// starting point, not a claim of completeness: any QEMU architecture
    /// not listed here either works fine with QEMU's own default (x86_64)
    /// or simply has no guided suggestion yet, same as before this field
    /// existed. The suggested machine type is only ever offered, never
    /// forced - see ArchitectureBox_SelectionChanged.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, (string MachineType, string Hint)> ArchHints =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["aarch64"] = ("virt", "ARM64 has no default machine type. 'virt' is QEMU's standard one; you'll also need a firmware image (e.g. an edk2 AAVMF 'QEMU_EFI.fd' build) unless booting a kernel directly via Extra arguments."),
            ["arm"] = ("virt", "32-bit ARM has no default machine type. 'virt' is QEMU's standard one for it."),
            ["riscv64"] = ("virt", "RISC-V has no default machine type. 'virt' is QEMU's standard one; you'll also need OpenSBI firmware unless booting a kernel directly via Extra arguments."),
            ["riscv32"] = ("virt", "RISC-V has no default machine type. 'virt' is QEMU's standard one for it."),
        };

    public VmEditWindow(VmConfig vm, string qemuDir)
    {
        InitializeComponent();
        _vm = vm;
        _qemuDir = qemuDir;

        PopulateArchitectures();
        LoadFromConfig();

        // Attached only after the initial population/load above so that
        // selecting the already-saved architecture here does not fight
        // with LoadFromConfig's own MachineTypeBox.Text assignment - see
        // ArchitectureBox_SelectionChanged's own remarks.
        ArchitectureBox.SelectionChanged += ArchitectureBox_SelectionChanged;
        RefreshArchHint(offerDefaultMachineType: false);
    }

    /// <summary>Shows a short hint for architectures known to need an
    /// explicit machine type/firmware, and - only when the user is actively
    /// changing the architecture, not on initial load - offers (never
    /// forces) that architecture's suggested machine type by filling it in
    /// if the field is currently empty. Never touches FirmwarePath: that is
    /// a real file only the user can supply, nothing here can guess it.</summary>
    private void RefreshArchHint(bool offerDefaultMachineType)
    {
        var arch = ArchitectureBox.SelectedItem?.ToString() ?? "";
        if (!ArchHints.TryGetValue(arch, out var hint))
        {
            ArchHintText.Text = "";
            return;
        }

        ArchHintText.Text = hint.Hint;
        if (offerDefaultMachineType && string.IsNullOrWhiteSpace(MachineTypeBox.Text))
            MachineTypeBox.Text = hint.MachineType;
    }

    private void ArchitectureBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RefreshArchHint(offerDefaultMachineType: true);

    private void PopulateArchitectures()
    {
        var archs = QemuLocator.ListArchitectures(_qemuDir);
        if (archs.Count == 0) archs = new() { _vm.Architecture }; // keep whatever was saved if QEMU is not where settings say

        foreach (var arch in archs)
            ArchitectureBox.Items.Add(arch);

        var index = archs.FindIndex(a => a.Equals(_vm.Architecture, StringComparison.OrdinalIgnoreCase));
        ArchitectureBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void LoadFromConfig()
    {
        NameBox.Text = _vm.Name;
        MemoryBox.Text = _vm.MemoryMB.ToString();
        CpuBox.Text = _vm.CpuCores.ToString();
        AccelBox.SelectedIndex = _vm.Accelerator == "whpx" ? 1 : 0;
        MachineTypeBox.Text = _vm.MachineType;
        FirmwareBox.Text = _vm.FirmwarePath;
        DiskBox.Text = _vm.DiskPath;
        CdromBox.Text = _vm.CdromPath;
        NetworkBox.SelectedIndex = _vm.NetworkMode == "none" ? 1 : 0;
        DisplayBox.SelectedIndex = _vm.Display switch { "sdl" => 1, "console" => 2, "vnc" => 3, _ => 0 };
        BootOrderBox.Text = _vm.BootOrder;
        ExtraArgsBox.Text = _vm.ExtraArgs;
    }

    private void DiskBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Disk images (*.qcow2;*.img;*.raw;*.vmdk;*.vdi;*.vhdx)|*.qcow2;*.img;*.raw;*.vmdk;*.vdi;*.vhdx|All files (*.*)|*.*",
            FileName = DiskBox.Text,
        };
        if (dialog.ShowDialog() == true) DiskBox.Text = dialog.FileName;
    }

    private void DiskCreateButton_Click(object sender, RoutedEventArgs e)
    {
        var suggestedName = string.IsNullOrWhiteSpace(NameBox.Text) ? "disk" : NameBox.Text;
        var suggestedPath = Path.Combine(AppPaths.AppDataDir, "disks", suggestedName + ".qcow2");
        Directory.CreateDirectory(Path.GetDirectoryName(suggestedPath)!);

        var dialog = new CreateDiskWindow(_qemuDir, suggestedPath) { Owner = this };
        if (dialog.ShowDialog() == true) DiskBox.Text = dialog.CreatedPath;
    }

    private void CdromBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*", FileName = CdromBox.Text };
        if (dialog.ShowDialog() == true) CdromBox.Text = dialog.FileName;
    }

    private void CdromClearButton_Click(object sender, RoutedEventArgs e) => CdromBox.Text = "";

    private void FirmwareBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Firmware/BIOS images (*.fd;*.bin;*.rom;*.elf)|*.fd;*.bin;*.rom;*.elf|All files (*.*)|*.*",
            FileName = FirmwareBox.Text,
        };
        if (dialog.ShowDialog() == true) FirmwareBox.Text = dialog.FileName;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationText.Text = "Name is required."; return; }
        if (!int.TryParse(MemoryBox.Text.Trim(), out var memory) || memory < 16)
        { ValidationText.Text = "Memory must be a number of MB, at least 16."; return; }
        if (!int.TryParse(CpuBox.Text.Trim(), out var cpus) || cpus < 1)
        { ValidationText.Text = "CPU cores must be a positive whole number."; return; }
        if (ArchitectureBox.SelectedItem == null)
        { ValidationText.Text = "Choose an architecture."; return; }
        var bootOrder = BootOrderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(bootOrder)) bootOrder = "c";

        _vm.Name = name;
        _vm.Architecture = ArchitectureBox.SelectedItem.ToString()!;
        _vm.MemoryMB = memory;
        _vm.CpuCores = cpus;
        _vm.Accelerator = AccelBox.SelectedIndex == 1 ? "whpx" : "tcg";
        _vm.MachineType = MachineTypeBox.Text.Trim();
        _vm.FirmwarePath = FirmwareBox.Text.Trim();
        _vm.DiskPath = DiskBox.Text.Trim();
        _vm.CdromPath = CdromBox.Text.Trim();
        _vm.NetworkMode = NetworkBox.SelectedIndex == 1 ? "none" : "user";
        _vm.Display = DisplayBox.SelectedIndex switch { 1 => "sdl", 2 => "console", 3 => "vnc", _ => "gtk" };
        _vm.BootOrder = bootOrder;
        _vm.ExtraArgs = ExtraArgsBox.Text.Trim();

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

using System.Windows;

namespace QemuManager;

public partial class NewVmKindWindow : Window
{
    /// <summary>"Qemu" or "RiscvEmu", set only when the dialog closes with
    /// DialogResult true.</summary>
    public string SelectedKind { get; private set; } = "Qemu";

    public NewVmKindWindow()
    {
        InitializeComponent();
    }

    private void QemuButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedKind = "Qemu";
        DialogResult = true;
    }

    private void RiscvEmuButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedKind = "RiscvEmu";
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

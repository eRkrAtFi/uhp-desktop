using System.Windows;

namespace QemuManager;

public partial class SettingsWindow : Window
{
    public int RiscvShutdownTimeoutSeconds { get; private set; }

    public SettingsWindow(int currentRiscvShutdownTimeoutSeconds)
    {
        InitializeComponent();
        RiscvShutdownTimeoutSeconds = currentRiscvShutdownTimeoutSeconds;
        RiscvShutdownTimeoutBox.Text = currentRiscvShutdownTimeoutSeconds.ToString();
        VersionText.Text = Services.AppVersion.DisplayString;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
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
            DialogResult = true;
        }
        else
        {
            ValidationText.Text = $"\"{timeoutText}\" is not a positive whole number of seconds.";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

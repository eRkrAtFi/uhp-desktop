using System.Windows;
using QemuManager.Models;
using QemuManager.Services;
using MessageBox = System.Windows.MessageBox;

namespace QemuManager;

/// <summary>
/// Live view of one running VM's text console - either riscv-emu's guest
/// UART (always console-only, there is no other display) or a real QEMU
/// VM whose Display is set to "console" (its serial port piped here
/// instead of opening a gtk/sdl window). Phase 5: a real terminal
/// (TerminalControl, xterm.js in WebView2) rather than a plain TextBox
/// with hand-rolled key interception - see TerminalControl's own remarks
/// for the message protocol and why input/output both go through it
/// directly now. There is deliberately no "Stop (graceful)" button when
/// the source is riscv-emu: see RiscvEmuProcessManager's class remarks for
/// why (no QMP-equivalent control channel exists there). For a real QEMU
/// VM, this window's Force Stop is still the hard-power-off fallback; the
/// main window's own Stop/Force Stop buttons remain the way to ask QEMU
/// for an ACPI shutdown instead.
/// </summary>
public partial class ConsoleWindow : Window
{
    private readonly EnvironmentConfig _vm;
    private readonly IVmService _source;

    // IVmService, not just IConsoleSource - the one extra thing this
    // window needs beyond IConsoleSource's own narrow surface is
    // GetConsoleBuffer, to seed its view with whatever Core already
    // captured before this window existed (a boot log that finished
    // scrolling by before anyone thought to open the console used to be
    // gone for good - see VmService.GetConsoleBuffer's own remarks).
    // Safe to require the wider interface: the only real caller
    // (MainWindow.OpenConsoleWindow) already has one and passes it.
    public ConsoleWindow(EnvironmentConfig vm, IVmService source)
    {
        InitializeComponent();
        _vm = vm;
        _source = source;
        var engine = vm.EngineId == "RiscvEmu" ? "riscv-emu" : "QEMU";
        TitleText.Text = $"{engine} - {vm.Name}";
        Title = $"{engine} console - {vm.Name}";

        // WebView2's own async initialization cannot happen in the
        // constructor (ShowDialog/Show must return promptly) - Loaded is
        // the first point a real HWND exists for it to attach to anyway.
        Loaded += async (_, _) =>
        {
            await Terminal.InitializeAsync();
            Terminal.Attach(_source, _vm.Id, _source.GetConsoleBuffer(_vm.Id), _vm.Debug.EnterSends);
        };
        // Phase 5: this window no longer closes itself when the VM stops
        // (TerminalControl shows a "VM stopped" banner instead, output
        // staying exactly as readable as it was) - only Detach its own
        // event subscriptions once this window itself closes.
        Closed += (_, _) => Terminal.Detach();
    }

    private void SendRaw(params byte[] bytes) => _source.SendInput(_vm.Id, bytes);

    private void SendCtrlCButton_Click(object sender, RoutedEventArgs e) => SendRaw(0x03);

    private void ForceStopButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, $"Force-terminate '{_vm.Name}'? This is equivalent to pulling the power cord.",
            "Force Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _source.ForceStop(_vm);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e) => Terminal.Clear();
}

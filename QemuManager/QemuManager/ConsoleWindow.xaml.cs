using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QemuManager.Models;
using QemuManager.Services;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace QemuManager;

/// <summary>
/// Live view of one running VM's text console - either riscv-emu's guest
/// UART (always console-only, there is no other display) or a real QEMU
/// VM whose Display is set to "console" (its serial port piped here
/// instead of opening a gtk/sdl window) - plus a best-effort input box.
/// There is deliberately no "Stop (graceful)" button when the source is
/// riscv-emu: see RiscvEmuProcessManager's class remarks for why (no QMP-
/// equivalent control channel exists there). For a real QEMU VM, this
/// window's Force Stop is still the hard-power-off fallback; the main
/// window's own Stop/Force Stop buttons remain the way to ask QEMU for an
/// ACPI shutdown instead.
/// </summary>
public partial class ConsoleWindow : Window
{
    // Keeps the UI responsive and memory bounded on a VM that runs for a
    // long time: an unbounded TextBox would both leak and get slower to
    // append to as it grows.
    private const int MaxRetainedChars = 500_000;

    private readonly VmConfig _vm;
    private readonly IConsoleSource _source;
    private readonly StringBuilder _buffer = new();

    public ConsoleWindow(VmConfig vm, IConsoleSource source)
    {
        InitializeComponent();
        _vm = vm;
        _source = source;
        var engine = vm.Kind == "RiscvEmu" ? "riscv-emu" : "QEMU";
        TitleText.Text = $"{engine} - {vm.Name}";
        Title = $"{engine} console - {vm.Name}";
        InputHintText.Text = vm.Kind == "RiscvEmu"
            ? "Input is sent to the guest's UART receive side - works once the guest has a getty or shell attached to it."
            : "Input is sent to the guest's serial port (COM1) - works once the guest has a getty or shell attached to it.";

        _source.OutputReceived += OnOutputReceived;
        _source.ProcessExited += OnProcessExited;
        Closed += (_, _) =>
        {
            _source.OutputReceived -= OnOutputReceived;
            _source.ProcessExited -= OnProcessExited;
        };
    }

    private void OnProcessExited(string vmId)
    {
        if (vmId != _vm.Id) return;
        // Raised on a background thread; give the guest's last output (a
        // shutdown banner, "exit"'s own echo, etc.) a brief moment to
        // actually render before the window disappears, rather than
        // closing the instant the process handle signals.
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(600);
            if (IsLoaded) Close();
        });
    }

    private void OnOutputReceived(string vmId, byte[] data)
    {
        if (vmId != _vm.Id) return;
        // Raw bytes, decoded 1:1 (not UTF-8) so an arbitrary byte from the
        // guest can never throw a decoding exception on this path.
        var text = Encoding.Latin1.GetString(data);
        Dispatcher.BeginInvoke(() => AppendOutput(text));
    }

    private void AppendOutput(string text)
    {
        _buffer.Append(text);
        if (_buffer.Length > MaxRetainedChars)
            _buffer.Remove(0, _buffer.Length - MaxRetainedChars);

        OutputBox.Text = _buffer.ToString();
        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.ScrollToEnd();
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => SendInput();

    private void InputBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) SendInput();
    }

    /// <summary>Stand-in for a real terminal's raw input mode until Phase 5
    /// builds one: specific control keys bypass InputBox's line buffering
    /// (type a line, Enter submits it) entirely and go straight to the
    /// guest as the single control byte (or VT100 escape sequence) a real
    /// terminal would send, with no Enter needed - this is what makes
    /// Ctrl+C able to interrupt something like a running `ping` before it
    /// finishes on its own, which line-buffered input never could (the
    /// guest's own line discipline would not even see Ctrl+C until a
    /// newline closed the line it arrived on). Wired to both InputBox and
    /// OutputBox's PreviewKeyDown - Ctrl+C specifically has to work from
    /// OutputBox too, since watching a runaway command's own output
    /// scroll by, not sitting in the input box, is exactly when someone
    /// reaches for it.</summary>
    private void TerminalKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (ctrl && shift && e.Key == Key.C)
        {
            // Always copies, regardless of what Ctrl+C alone would do -
            // the explicit "I mean copy" shortcut real terminals offer
            // precisely because plain Ctrl+C is overloaded with "send
            // interrupt" there too.
            CopyFocusedSelection();
            e.Handled = true;
            return;
        }
        if (ctrl && !shift && e.Key == Key.C)
        {
            // Overloaded exactly the way a real terminal overloads it:
            // text selected anywhere in this window means "copy", so fall
            // through to TextBox's own default Ctrl+C handling (not
            // marking this handled) instead of sending anything; nothing
            // selected means "send the interrupt byte".
            if (Keyboard.FocusedElement is TextBox { } focused && !string.IsNullOrEmpty(focused.SelectedText)) return;
            SendRaw(0x03);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.D) { SendRaw(0x04); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Z) { SendRaw(0x1A); e.Handled = true; return; }
        if (ctrl && e.Key == Key.L) { SendRaw(0x0C); e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Tab: SendRaw(0x09); e.Handled = true; break;
            case Key.Escape: SendRaw(0x1B); e.Handled = true; break;
            // Standard VT100 cursor sequences (ESC [ A/B/C/D) - what a
            // real terminal sends for these, and what a shell's own line
            // editor (command history, in-line cursor movement) expects
            // to receive, not a bare unescaped arrow keycode.
            case Key.Up: SendRaw(0x1B, (byte)'[', (byte)'A'); e.Handled = true; break;
            case Key.Down: SendRaw(0x1B, (byte)'[', (byte)'B'); e.Handled = true; break;
            case Key.Right: SendRaw(0x1B, (byte)'[', (byte)'C'); e.Handled = true; break;
            case Key.Left: SendRaw(0x1B, (byte)'[', (byte)'D'); e.Handled = true; break;
        }
    }

    private void CopyFocusedSelection()
    {
        if (Keyboard.FocusedElement is TextBox { } focused && !string.IsNullOrEmpty(focused.SelectedText))
            Clipboard.SetText(focused.SelectedText);
    }

    private void SendRaw(params byte[] bytes) => _source.SendInput(_vm.Id, bytes);

    private void SendCtrlCButton_Click(object sender, RoutedEventArgs e) => SendRaw(0x03);

    private void SendInput()
    {
        var text = InputBox.Text;
        InputBox.Clear();
        // Confirmed against a real guest (TinyCore's busybox ash on a
        // getty-attached ttyS0): the assumption that ICRNL would translate
        // a CR into a line feed on the way in was wrong - sending "\r"
        // alone never submits the line at all, so every subsequent send
        // lands on the *same*, still-open input line, looking like
        // commands silently concatenating. "\n" alone is what the guest's
        // tty line discipline actually treats as end-of-line here.
        var bytes = Encoding.Latin1.GetBytes(text + "\n");
        _source.SendInput(_vm.Id, bytes);
    }

    private void ForceStopButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(this, $"Force-terminate '{_vm.Name}'? This is equivalent to pulling the power cord.",
            "Force Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _source.ForceStop(_vm);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _buffer.Clear();
        OutputBox.Text = "";
    }
}

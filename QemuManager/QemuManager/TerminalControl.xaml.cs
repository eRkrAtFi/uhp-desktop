using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using QemuManager.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace QemuManager;

/// <summary>
/// Phase 5's real terminal (xterm.js in WebView2 - see Assets\terminal.html
/// for the actual page and message protocol) as one reusable control, so
/// ConsoleWindow and the VM detail page's own console preview share the
/// exact same implementation rather than two separate ones drifting apart.
/// The detail page uses it read-only (see ReadOnly) - a live stream still,
/// just with no cursor/keyboard input, since nothing there has ever let the
/// user type into the preview.
///
/// Lifecycle: construct, set ReadOnly if needed, await InitializeAsync()
/// once (creates the WebView2 control and navigates it), then Attach() per
/// VM (and Detach() before attaching a different one, or when done) - kept
/// separate from InitializeAsync so a window that reuses one control across
/// several VMs (the detail page, switching between them) does not re-pay
/// WebView2's own startup cost each time.
/// </summary>
public partial class TerminalControl : UserControl
{
    private IConsoleSource? _source;
    private string? _vmId;
    private string _enterSends = "CR";
    private bool _jsReady;
    // Buffered until the JS side signals "ready" (term created, fit done) -
    // output can start arriving (OutputReceived) before WebView2 finishes
    // navigating and running terminal.html's own script, and losing the
    // very start of a reattached VM's live output would be exactly the
    // "boot log that scrolled by before anyone opened this" bug the
    // replay buffer already exists to avoid.
    private readonly Queue<byte[]> _pendingOutput = new();

    public bool ReadOnly { get; set; }

    public TerminalControl()
    {
        InitializeComponent();
    }

    /// <summary>Creates the WebView2 control and navigates it to
    /// terminal.html - once per control instance, before the first
    /// Attach(). UserDataFolder is explicitly AppPaths.AppDataDir\webview2,
    /// not WebView2's own default (a folder next to the .exe) - this app
    /// is meant to run with no admin rights and no guaranteed write access
    /// next to its own .exe (e.g. installed under Program Files), the
    /// same reasoning AppPaths itself already exists for.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(AppPaths.AppDataDir, "webview2");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await Webview.EnsureCoreWebView2Async(env);

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            Webview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "appassets.local", assetsDir, CoreWebView2HostResourceAccessKind.Allow);
            Webview.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            var query = ReadOnly ? "?readonly=1" : "";
            Webview.CoreWebView2.Navigate($"https://appassets.local/terminal.html{query}");
        }
        catch (Exception ex)
        {
            // WebView2 Runtime missing or failed to initialize - this
            // app's own Requirements already list Windows 10/11, which
            // ships it by default since 2022, so this is the unusual
            // path, not the expected one. A clear message instead of an
            // unhandled exception taking the whole window down with it.
            Webview.Visibility = Visibility.Collapsed;
            UnavailableText.Visibility = Visibility.Visible;
            UnavailableText.Text = "Could not start the terminal (WebView2 Runtime missing or failed to " +
                "initialize). Install it from https://developer.microsoft.com/microsoft-edge/webview2/ " +
                $"and restart this app.\n\nDetails: {ex.Message}";
        }
    }

    /// <summary>Starts showing vmId's own console - replays whatever
    /// Core already captured (GetConsoleBuffer), then streams live output
    /// as it arrives. Call Detach() first if this control was already
    /// showing a different VM. enterSends is that VM's own
    /// VmConfig.EnterSends (what the terminal's Enter key should actually
    /// send down this specific console - see its own remarks) - re-sent
    /// to terminal.html on every call since the detail page's own preview
    /// reuses one control across VMs that can each have a different
    /// setting.</summary>
    public void Attach(IConsoleSource source, string vmId, string alreadyCapturedBuffer, string enterSends = "CR")
    {
        // Clears whatever a previous VM's own Attach left behind first -
        // without this, switching the detail page between two VMs would
        // show the old one's leftover lines followed by the new one's own
        // replay buffer, not a clean switch. A ConsoleWindow (one VM for
        // its whole lifetime) never actually exercises this path, but the
        // detail page's own preview, reused across VM selections, does.
        Clear();
        _source = source;
        _vmId = vmId;
        _enterSends = enterSends;
        _source.OutputReceived += OnOutputReceived;
        _source.ProcessExited += OnProcessExited;
        SendEnterSends();

        if (alreadyCapturedBuffer.Length > 0)
        {
            // Latin1 round-trips 1:1 back to the exact original bytes
            // VmService.AppendToConsoleBuffer stored (it decoded the
            // guest's own raw bytes the same way, for the same reason -
            // see its own remarks) - not re-encoding this string as UTF-8
            // directly, which would double-encode any byte already above
            // 127 instead of reproducing it. terminal.html's own xterm.js
            // then decodes these raw bytes as UTF-8 itself, same as live
            // output below.
            EnqueueOutput(Encoding.Latin1.GetBytes(alreadyCapturedBuffer));
        }
    }

    /// <summary>Clears the scrollback and hides the "VM stopped" banner if
    /// it was showing - the Clear button's own action, same as the old
    /// OutputBox-based ConsoleWindow's. Also drops anything still queued
    /// in _pendingOutput (not yet flushed because the JS side was not
    /// ready) - Attach calls this before queuing a different VM's own
    /// replay buffer, and a previous VM's leftover queued bytes must not
    /// show up interleaved with it once the page does become ready. Safe
    /// to call before the JS side is ready at all: there is nothing
    /// visible yet to clear there in that case (only _pendingOutput could
    /// have anything in it), so the message is simply skipped rather than
    /// posted into a listener that does not exist yet.</summary>
    public void Clear()
    {
        _pendingOutput.Clear();
        if (_jsReady) PostMessage(new { type = "clear" });
    }

    public void Detach()
    {
        if (_source != null)
        {
            _source.OutputReceived -= OnOutputReceived;
            _source.ProcessExited -= OnProcessExited;
        }
        _source = null;
        _vmId = null;
    }

    private void OnOutputReceived(string vmId, byte[] data)
    {
        if (vmId != _vmId) return;
        EnqueueOutput(data);
    }

    private void OnProcessExited(string vmId)
    {
        if (vmId != _vmId) return;
        // Does NOT close/clear anything - see this control's own class
        // remarks and the user-facing "VM stopped" banner terminal.html
        // shows on this message. The guest's last output (a shutdown
        // banner, "exit"'s own echo, ...) stays exactly as readable as it
        // was the instant before this fired.
        Dispatcher.BeginInvoke(() => PostMessage(new { type = "stopped" }));
    }

    private void EnqueueOutput(byte[] data)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_jsReady) { _pendingOutput.Enqueue(data); return; }
            PostOutput(data);
        });
    }

    private void PostOutput(byte[] data) =>
        PostMessage(new { type = "output", data = Convert.ToBase64String(data) });

    // Only meaningful once the JS side is ready (same reasoning as
    // PostMessage's own CoreWebView2-null check) - if Attach ran before
    // that, OnWebMessageReceived's own "ready" case calls this again once
    // it actually can be delivered, using whatever _enterSends Attach
    // already set.
    private void SendEnterSends()
    {
        if (_jsReady) PostMessage(new { type = "enterSends", value = _enterSends });
    }

    private void PostMessage(object message)
    {
        if (Webview.CoreWebView2 == null) return;
        Webview.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var type = doc.RootElement.GetProperty("type").GetString();
        switch (type)
        {
            case "ready":
                _jsReady = true;
                SendEnterSends();
                while (_pendingOutput.Count > 0) PostOutput(_pendingOutput.Dequeue());
                break;
            case "input":
                // UTF-8, not Latin1 - this is live keyboard input xterm.js
                // itself produced (Ctrl+C -> \x03, arrows -> ESC[A/B/C/D,
                // pasted text verbatim, ...), always plain ASCII/UTF-8 by
                // construction, unlike the guest's own arbitrary output
                // bytes replayed/streamed above.
                var text = doc.RootElement.GetProperty("data").GetString() ?? "";
                _source?.SendInput(_vmId!, Encoding.UTF8.GetBytes(text));
                break;
        }
    }
}

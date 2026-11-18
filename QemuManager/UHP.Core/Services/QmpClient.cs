using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace QemuManager.Services;

/// <summary>
/// A minimal client for QEMU's own management protocol, QMP
/// (https://wiki.qemu.org/Documentation/QMP): newline-delimited JSON over a
/// TCP socket that QEMU listens on when started with
/// <c>-qmp tcp:127.0.0.1:&lt;port&gt;,server,nowait</c>.
///
/// This is what lets the app ask a real question ("is this VM actually
/// running, or did the guest crash?") and issue a graceful ACPI shutdown,
/// instead of only ever being able to kill the process.
/// </summary>
public class QmpClient : IAsyncDisposable
{
    /// <summary>How long any single QMP round trip (the initial greeting,
    /// the capabilities handshake, or any later command) is allowed to wait
    /// for QEMU before giving up - QEMU's own commands normally reply
    /// near-instantly, so this is "clearly broken," not "a bit slow." Picked
    /// to match VmProcessManager's existing 8-second ACPI-revert window
    /// rather than inventing an unrelated number. Without this, a QEMU that
    /// accepted the TCP connection but then hung (or simply never speaks
    /// QMP) left every caller - Stop (ACPI), live status polling - awaiting
    /// a ReadLineAsync that would never complete (ARCHITECTURE_AUDIT.md
    /// recommendation #4).</summary>
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    public async Task ConnectAsync(int port, TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;

        _tcp = new TcpClient();
        try
        {
            using var connectCts = new CancellationTokenSource(effectiveTimeout);
            await _tcp.ConnectAsync("127.0.0.1", port, connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Could not connect to QMP on port {port} within {effectiveTimeout.TotalSeconds:0}s.");
        }

        _stream = _tcp.GetStream();
        _reader = new StreamReader(_stream, Encoding.UTF8);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        // QEMU greets every new QMP connection with {"QMP": {...}} before it
        // will accept any command.
        await ReadLineWithTimeoutAsync(effectiveTimeout);

        // The capabilities handshake is mandatory; QEMU rejects every other
        // command until this succeeds.
        await ExecuteAsync("qmp_capabilities", timeout: effectiveTimeout);
    }

    /// <summary>Sends one QMP command and returns its "return" payload.
    /// Throws if QEMU answers with an "error" object instead, or if it
    /// does not answer at all within <paramref name="timeout"/> (default:
    /// see <see cref="DefaultTimeout"/>).</summary>
    public async Task<JsonElement> ExecuteAsync(string command, object? arguments = null, TimeSpan? timeout = null)
    {
        if (_writer == null || _reader == null)
            throw new InvalidOperationException("QmpClient is not connected.");

        var effectiveTimeout = timeout ?? DefaultTimeout;
        string requestJson = arguments == null
            ? JsonSerializer.Serialize(new { execute = command })
            : JsonSerializer.Serialize(new { execute = command, arguments });
        await _writer.WriteLineAsync(requestJson);

        // QEMU can interleave asynchronous "event" notifications with command
        // replies; skip events and keep reading until the matching reply.
        while (true)
        {
            var line = await ReadLineWithTimeoutAsync(effectiveTimeout);
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("event", out _)) continue;

            if (root.TryGetProperty("error", out var error))
            {
                var desc = error.TryGetProperty("desc", out var d) ? d.GetString() : "unknown QMP error";
                throw new InvalidOperationException($"QMP command '{command}' failed: {desc}");
            }
            if (root.TryGetProperty("return", out var ret)) return ret.Clone();
            // Neither error nor return: not a reply to our command, keep reading.
        }
    }

    /// <summary>One line, or throws TimeoutException after <paramref
    /// name="timeout"/> instead of waiting forever, and IOException if the
    /// connection closed cleanly instead of answering at all.</summary>
    private async Task<string> ReadLineWithTimeoutAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        string? line;
        try
        {
            line = await _reader!.ReadLineAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"QMP did not respond within {timeout.TotalSeconds:0}s.");
        }
        if (line == null) throw new IOException("QMP connection closed unexpectedly.");
        return line;
    }

    /// <summary>Returns QEMU's run state: "running", "paused", "shutdown", etc.</summary>
    public async Task<string> QueryStatusAsync()
    {
        var result = await ExecuteAsync("query-status");
        return result.TryGetProperty("status", out var s) ? s.GetString() ?? "unknown" : "unknown";
    }

    /// <summary>Requests an ACPI power-down, the same as pressing the power
    /// button on real hardware - the guest OS decides whether and how to shut
    /// down, and may take some time or never respond.</summary>
    public Task PowerdownAsync() => ExecuteAsync("system_powerdown");

    /// <summary>Asks QEMU to exit immediately. Still graceful from the host's
    /// perspective (clean process exit), but not from the guest's.</summary>
    public Task QuitAsync() => ExecuteAsync("quit");

    public async ValueTask DisposeAsync()
    {
        _writer?.Dispose();
        _reader?.Dispose();
        if (_stream != null) await _stream.DisposeAsync();
        _tcp?.Dispose();
    }
}

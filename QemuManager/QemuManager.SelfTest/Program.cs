// End-to-end smoke test of QemuManager's control plane against real QEMU:
// start a VM with no disk attached (so no guest OS is needed), confirm QMP
// reports it running, request a graceful ACPI power-down over QMP, then
// force-stop as a fallback and confirm the process is gone. This is not part
// of the shipped app - it is the harness that proves VmProcessManager and
// QmpClient actually work against the real qemu-system-*.exe, not just that
// they compile.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using QemuManager.Models;
using QemuManager.Services;

// Must be set before anything touches AppPaths (whose AppDataDir is a
// static field computed once, on first access) - without this, the very
// first run of this freshly-built .exe would silently clone the real
// user's %APPDATA%\QemuManager (VM configs, settings) into this project's
// own bin output the moment it finds no "data" folder there yet (see
// AppPaths.ResolveAppDataDir's own remarks). This is what actually
// happened once already with a different build, and the VM it created
// there was mistaken for a real one - this test must never read or write
// anything a real user's app instance would ever see.
Environment.SetEnvironmentVariable("QEMUMANAGER_DATA_DIR", Path.Combine(Path.GetTempPath(), "QemuManagerSelfTestData"));

var qemuDir = args.Length > 0 ? args[0] : @"C:\Program Files\qemu";
// riscv-emu/ lives as a sibling of QemuManager/ in this repo, so this
// relative guess resolves correctly when run the documented way (`dotnet
// run` from the QemuManager.SelfTest directory) without hardcoding an
// absolute path; pass it explicitly as a second argument if it lives
// somewhere else.
var riscvEmuDir = args.Length > 1 ? args[1] : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "riscv-emu"));
Console.WriteLine($"QEMU dir: {qemuDir}");
Console.WriteLine($"LooksLikeQemuDir: {QemuLocator.LooksLikeQemuDir(qemuDir)}");

var archs = QemuLocator.ListArchitectures(qemuDir);
Console.WriteLine($"Architectures found: {archs.Count} (first few: {string.Join(", ", archs.Take(5))})");

var vm = new VmConfig
{
    Name = "selftest-vm",
    Architecture = "x86_64",
    MemoryMB = 256,
    CpuCores = 1,
    Display = "none-like", // overwritten below; QEMU's actual value is "none"
};
// "none" is not one of the two choices the GUI offers (gtk/sdl), but it is a
// real QEMU display backend and the right one for a headless smoke test.
vm.Display = "none";

var manager = new VmProcessManager();

Console.WriteLine("\n--- Start ---");
var startResult = await manager.StartAsync(vm, qemuDir);
Console.WriteLine(startResult);
Console.WriteLine($"State after start: {manager.GetState(vm.Id)}");

if (manager.GetState(vm.Id) == VmRunState.Running)
{
    Console.WriteLine("\n--- Query guest status over QMP ---");
    var status = await manager.QueryGuestStatusAsync(vm);
    Console.WriteLine($"query-status: {status}");

    Console.WriteLine("\n--- Graceful stop (ACPI powerdown over QMP) ---");
    var stopMsg = await manager.StopGracefullyAsync(vm);
    Console.WriteLine(stopMsg);

    // A VM with no OS and no power button driver never actually powers off
    // from ACPI alone, so this is expected to still be running - which is
    // exactly why the app also offers Force Stop.
    await Task.Delay(1500);
    Console.WriteLine($"State 1.5s after ACPI request: {manager.GetState(vm.Id)}");

    Console.WriteLine("\n--- Force stop ---");
    var forceMsg = manager.ForceStop(vm);
    Console.WriteLine(forceMsg);
    await Task.Delay(500);
    Console.WriteLine($"State after force stop: {manager.GetState(vm.Id)}");
}

Console.WriteLine("\n--- Reattach after simulated app restart ---");
// Proves RuntimeStateStore/TryReattach against a real process, not just
// that they compile: start a VM with one VmProcessManager, then hand it to
// a brand-new instance - standing in for "the app restarted" - and confirm
// that instance can discover it is still running, query it over QMP, and
// stop it, none of which it could do with only its own (empty) in-memory
// state. See ARCHITECTURE_AUDIT.md's Critical Risk #1.
var vm2 = new VmConfig { Name = "selftest-reattach-vm", Architecture = "x86_64", MemoryMB = 256, CpuCores = 1, Display = "none" };
var firstInstanceManager = new VmProcessManager();
Console.WriteLine(await firstInstanceManager.StartAsync(vm2, qemuDir));
Console.WriteLine($"State in the instance that started it: {firstInstanceManager.GetState(vm2.Id)}");

var secondInstanceManager = new VmProcessManager();
Console.WriteLine($"Before reattach, a fresh instance reports: {secondInstanceManager.GetState(vm2.Id)}");
var reattached = secondInstanceManager.TryReattach(vm2);
Console.WriteLine($"TryReattach returned: {reattached}");
Console.WriteLine($"After reattach, the fresh instance reports: {secondInstanceManager.GetState(vm2.Id)}");

if (reattached)
{
    var status = await secondInstanceManager.QueryGuestStatusAsync(vm2);
    Console.WriteLine($"query-status via the reattached QMP port: {status}");

    var forceMsg = secondInstanceManager.ForceStop(vm2);
    Console.WriteLine($"Force stop via the second instance: {forceMsg}");
    await Task.Delay(500);
    Console.WriteLine($"State after: {secondInstanceManager.GetState(vm2.Id)}");
}

var neverStartedVm = new VmConfig { Name = "selftest-never-started" };
Console.WriteLine($"TryReattach for a VM that was never started: {secondInstanceManager.TryReattach(neverStartedVm)} (expected: False)");

Console.WriteLine($"\nriscv-emu dir: {riscvEmuDir}");
Console.WriteLine("\n--- RiscvEmu engine (ARCHITECTURE_AUDIT.md recommendation #5) ---");
// Before this, nothing in this harness (or anywhere else in the repo)
// exercised RiscvEmuProcessManager/RiscvEmuBoot against the real
// riscv-emu.exe - only the GUI did, manually. Mirrors the QEMU engine's
// own Start/resource-usage/reattach coverage above.
if (RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvEmuDir))
{
    var kernelPath = Path.Combine(riscvEmuDir, "boot", "KernelImage");
    var initrdPath = Path.Combine(riscvEmuDir, "boot", "initramfs.cpio.gz");
    if (!File.Exists(kernelPath))
    {
        Console.WriteLine($"No kernel Image found at '{kernelPath}' - skipping.");
    }
    else
    {
        var riscvVm = new VmConfig
        {
            Kind = "RiscvEmu",
            Name = "selftest-riscv-emu",
            KernelPath = kernelPath,
            InitrdPath = File.Exists(initrdPath) ? initrdPath : "",
        };

        var riscvManager1 = new RiscvEmuProcessManager();
        Console.WriteLine(riscvManager1.Start(riscvVm, riscvEmuDir));
        await Task.Delay(500); // let riscv-emu run for a moment before sampling it
        Console.WriteLine($"State: {(riscvManager1.IsRunning(riscvVm.Id) ? "Running" : "Stopped")}");

        var usage = riscvManager1.GetResourceUsage(riscvVm.Id);
        Console.WriteLine(usage is { } u
            ? $"Resource usage: {u.cpuPercent:0.#}% CPU, {u.memoryBytes / 1024.0 / 1024.0:0.#} MB"
            : "Resource usage: null (unexpected while running)");

        Console.WriteLine("--- RiscvEmu reattach after simulated app restart ---");
        var riscvManager2 = new RiscvEmuProcessManager();
        Console.WriteLine($"Before reattach, a fresh instance reports: {(riscvManager2.IsRunning(riscvVm.Id) ? "Running" : "Stopped")}");
        var riscvReattached = riscvManager2.TryReattach(riscvVm);
        Console.WriteLine($"TryReattach returned: {riscvReattached}");
        Console.WriteLine($"After reattach: {(riscvManager2.IsRunning(riscvVm.Id) ? "Running" : "Stopped")}");
        if (riscvReattached)
        {
            var usage2 = riscvManager2.GetResourceUsage(riscvVm.Id);
            Console.WriteLine(usage2 is { } u2
                ? $"Resource usage via reattached instance: {u2.cpuPercent:0.#}% CPU, {u2.memoryBytes / 1024.0 / 1024.0:0.#} MB"
                : "Resource usage via reattached instance: null (unexpected)");
            Console.WriteLine(riscvManager2.ForceStop(riscvVm));
            await Task.Delay(300);
            Console.WriteLine($"State after force stop: {(riscvManager2.IsRunning(riscvVm.Id) ? "Running" : "Stopped")}");
        }

        var riscvNeverStarted = new VmConfig { Kind = "RiscvEmu", Name = "selftest-riscv-never-started" };
        Console.WriteLine($"TryReattach for a RiscvEmu VM that was never started: {riscvManager2.TryReattach(riscvNeverStarted)} (expected: False)");

        // --- Scenario coverage requested after Phase 0's UI overhaul: does
        // a real shell boot actually work end to end through the new
        // --kernel/--initrd/--disk/--net flags and the console/stats
        // channel split, not just "the process starts and GetState says
        // Running" (which the block above already covered but which says
        // nothing about whether a human watching the console would see a
        // working VM). -----------------------------------------------------
        async Task<(string vmId, StringBuilder console, RiscvEmuProcessManager mgr)> BootToShell(VmConfig vm, int timeoutMs)
        {
            var mgr = new RiscvEmuProcessManager();
            var console = new StringBuilder();
            mgr.OutputReceived += (id, bytes) => { if (id == vm.Id) lock (console) console.Append(Encoding.UTF8.GetString(bytes)); };
            var startResult = mgr.Start(vm, riscvEmuDir);
            Console.WriteLine($"Start: {startResult}");
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                lock (console) { if (console.ToString().Contains("~ #")) break; }
                if (!mgr.IsRunning(vm.Id)) break;
                await Task.Delay(200);
            }
            return (vm.Id, console, mgr);
        }

        void SendLine(RiscvEmuProcessManager mgr, string vmId, string line) =>
            mgr.SendInput(vmId, Encoding.ASCII.GetBytes(line + "\n"));

        async Task<string> WaitForNewText(StringBuilder console, int fromLength, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300);
                lock (console) { if (console.Length > fromLength) return console.ToString(fromLength, console.Length - fromLength); }
            }
            lock (console) { return console.ToString(fromLength, console.Length - fromLength); }
        }

        // Unlike WaitForNewText, this does not return the instant ANY new byte
        // shows up - it keeps polling until `found` matches or the timeout is
        // used up. That distinction matters once a reply can arrive well after
        // the command's own echo: WaitForNewText would return with just the
        // echoed "ping -c 1 10.0.2.2" line (new text, by its definition) before
        // the actual reply has had time to come back, which is exactly the
        // flakiness Scenario 3's ping check had - a race against the echo, not
        // a real check of the reply.
        async Task<string> WaitForText(StringBuilder console, int fromLength, int timeoutMs, Func<string, bool> found)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                string text;
                lock (console) { text = console.ToString(fromLength, console.Length - fromLength); }
                if (found(text) || DateTime.UtcNow >= deadline) return text;
                await Task.Delay(300);
            }
        }

        Console.WriteLine("\n--- Scenario 1: does it actually reach a shell, and does `uname -a` work? ---");
        {
            var vm1 = new VmConfig { Kind = "RiscvEmu", Name = "riscv-emu demo", KernelPath = kernelPath, InitrdPath = File.Exists(initrdPath) ? initrdPath : "" };
            var (vmId, console, mgr) = await BootToShell(vm1, 20000);
            var reachedShell = console.ToString().Contains("~ #");
            Console.WriteLine($"Reached '~ #' prompt: {reachedShell}");
            if (reachedShell)
            {
                var before = console.Length;
                SendLine(mgr, vmId, "uname -a");
                var reply = await WaitForNewText(console, before, 4000);
                Console.WriteLine($"uname -a reply contains 'riscv64': {reply.Contains("riscv64")} (raw: {reply.Trim()})");
            }
            mgr.ForceStop(vm1);
            await Task.Delay(300);
        }

        Console.WriteLine("\n--- Scenario 2: a VM saved before DiskPath/NetworkMode/MemoryMB existed loads with sensible defaults ---");
        {
            // Hand-written, deliberately missing every field this session
            // added, the same way a real pre-existing vms\<id>.json saved by
            // an older build of this app would be - System.Text.Json fills
            // anything absent from the default object initializer, not from
            // the all-zero/all-null default a struct would get, so this is
            // the actual behavior a real upgrade hits, not a weaker stand-in.
            var oldJson = $$"""
            {
              "Id": "selftest-old-riscv-vm",
              "Name": "riscv-emu demo (old save)",
              "Kind": "RiscvEmu",
              "KernelPath": "{{kernelPath.Replace("\\", "\\\\")}}",
              "InitrdPath": "{{(File.Exists(initrdPath) ? initrdPath.Replace("\\", "\\\\") : "")}}"
            }
            """;
            var oldVm = System.Text.Json.JsonSerializer.Deserialize<VmConfig>(oldJson)!;
            Console.WriteLine($"Deserialized old-format VM - DiskPath='{oldVm.DiskPath}' NetworkMode='{oldVm.NetworkMode}' MemoryMB={oldVm.MemoryMB}");
            var (vmId, console, mgr) = await BootToShell(oldVm, 20000);
            Console.WriteLine($"Old-format VM reached '~ #' prompt: {console.ToString().Contains("~ #")}");
            mgr.ForceStop(oldVm);
            await Task.Delay(300);
        }

        Console.WriteLine("\n--- Scenario 3: disk + network attached from the app - `ls /dev/vda` and `ping 10.0.2.2` in the guest ---");
        {
            var diskPath3 = Path.Combine(riscvEmuDir, "boot", "disk.img");
            if (!File.Exists(diskPath3)) await File.WriteAllBytesAsync(diskPath3, new byte[16 * 1024 * 1024]);
            var vm3 = new VmConfig
            {
                Kind = "RiscvEmu", Name = "selftest-disk-net",
                KernelPath = kernelPath, InitrdPath = File.Exists(initrdPath) ? initrdPath : "",
                DiskPath = diskPath3, NetworkMode = "user",
            };
            var (vmId, console, mgr) = await BootToShell(vm3, 20000);
            var reachedShell = console.ToString().Contains("~ #");
            Console.WriteLine($"Reached '~ #' prompt: {reachedShell}");
            if (reachedShell)
            {
                var before = console.Length;
                SendLine(mgr, vmId, "ls /dev/vda");
                var lsReply = await WaitForNewText(console, before, 4000);
                Console.WriteLine($"ls /dev/vda reply: {lsReply.Trim()}");

                before = console.Length;
                SendLine(mgr, vmId, "ping -c 1 10.0.2.2");
                // 8s, not 5s: a realistic ceiling for a DHCP lease + NAT round
                // trip under host load (manually confirmed well under 1s on an
                // idle host), not just enough for the command's own echo to
                // show up - see WaitForText's own remarks on why that distinction
                // is what makes this check reliable across repeated runs.
                var pingReply = await WaitForText(console, before, 8000, t => t.Contains("1 packets transmitted"));
                Console.WriteLine($"ping -c 1 10.0.2.2 reply contains '1 packets transmitted': {pingReply.Contains("1 packets transmitted")} (raw: {pingReply.Trim()})");
            }
            mgr.ForceStop(vm3);
            await Task.Delay(300);
        }

        Console.WriteLine("\n--- Scenario 4: a bad --kernel path must surface an error, not vanish as an unrecognized stderr line ---");
        {
            var vm4 = new VmConfig { Kind = "RiscvEmu", Name = "selftest-bad-kernel", KernelPath = Path.Combine(riscvEmuDir, "boot", "ThisKernelDoesNotExist") };
            var mgr = new RiscvEmuProcessManager();
            var console = new StringBuilder();
            mgr.OutputReceived += (id, bytes) => { if (id == vm4.Id) lock (console) console.Append(Encoding.UTF8.GetString(bytes)); };
            Console.WriteLine(mgr.Start(vm4, riscvEmuDir));
            await Task.Delay(1500);
            Console.WriteLine($"State after bad kernel path: {(mgr.IsRunning(vm4.Id) ? "Running (unexpected)" : "Stopped")}");
            lock (console)
                Console.WriteLine(console.Length > 0
                    ? $"Error surfaced to the console view: {console.ToString().Trim()}"
                    : "NOTHING reached the console view - this is the bug being checked for.");
        }

        Console.WriteLine("\n--- Scenario 5: guest shutdown via VmConfig.ShutdownCommand - VM must go Stopped, console must not vanish before the last output is readable ---");
        {
            // Plain "poweroff" is a confirmed no-op on this bundled busybox
            // image - verified identical under riscv-emu AND real
            // qemu-system-riscv64 (same SigBlk/SigCgt masks on PID 1, same
            // nothing-happens result on both: busybox's poweroff signals
            // init, and this image's init never acts on it). So this uses
            // the VM's own configured ShutdownCommand (VmConfig's default:
            // "sync; poweroff -f") instead of hardcoding "poweroff" - the
            // same thing the real Shutdown action will do once Phase 4 wires
            // it up.
            var vm5 = new VmConfig { Kind = "RiscvEmu", Name = "selftest-poweroff", KernelPath = kernelPath, InitrdPath = File.Exists(initrdPath) ? initrdPath : "" };
            var (vmId, console, mgr) = await BootToShell(vm5, 20000);
            var exited = false;
            mgr.ProcessExited += id => { if (id == vmId) exited = true; };
            if (console.ToString().Contains("~ #"))
            {
                var before = console.Length;
                SendLine(mgr, vmId, vm5.ShutdownCommand);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < deadline && !exited) await Task.Delay(200);
                var tail = console.ToString(before, console.Length - before);
                Console.WriteLine($"Process exited after '{vm5.ShutdownCommand}': {exited}");
                Console.WriteLine($"State: {(mgr.IsRunning(vmId) ? "Running (unexpected)" : "Stopped")}");
                Console.WriteLine($"Console output after shutdown (last output must be present, e.g. 'Power down'): {tail.Trim()}");
            }
            else
            {
                Console.WriteLine("Never reached a shell - cannot test shutdown. Forcing stop.");
                mgr.ForceStop(vm5);
            }
        }
    }
}
else
{
    Console.WriteLine($"riscv-emu.exe or boot\\firmware.bin not found in '{riscvEmuDir}' - skipping. Pass it as a second argument to `dotnet run` if it lives elsewhere.");
}

Console.WriteLine("\n--- Machine type (ARCHITECTURE_AUDIT.md recommendation #3) ---");
// Proves VmConfig.MachineType actually reaches qemu-system-aarch64.exe as
// -M virt and is accepted, rather than only confirming BuildArguments
// compiles. aarch64 has no usable default machine type at all, so a VM
// with MachineType left empty is expected to fail; one with it set to
// "virt" is expected to start and accept QMP connections, same as the
// x86_64 smoke test above.
if (QemuLocator.ListArchitectures(qemuDir).Contains("aarch64", StringComparer.OrdinalIgnoreCase))
{
    var armNoMachine = new VmConfig { Name = "selftest-arm-no-machine", Architecture = "aarch64", MemoryMB = 256, CpuCores = 1, Display = "none" };
    var armManager1 = new VmProcessManager();
    Console.WriteLine($"aarch64, no MachineType set: {await armManager1.StartAsync(armNoMachine, qemuDir)}");
    await Task.Delay(500);
    Console.WriteLine($"State (expected: Stopped - qemu-system-aarch64 refuses to start with no machine type): {armManager1.GetState(armNoMachine.Id)}");

    var armWithMachine = new VmConfig { Name = "selftest-arm-virt", Architecture = "aarch64", MachineType = "virt", MemoryMB = 256, CpuCores = 1, Display = "none" };
    var armManager2 = new VmProcessManager();
    Console.WriteLine($"aarch64, MachineType \"virt\": {await armManager2.StartAsync(armWithMachine, qemuDir)}");
    Console.WriteLine($"State (expected: Running): {armManager2.GetState(armWithMachine.Id)}");
    if (armManager2.GetState(armWithMachine.Id) == VmRunState.Running)
        Console.WriteLine($"query-status: {await armManager2.QueryGuestStatusAsync(armWithMachine)}");
    Console.WriteLine(armManager2.ForceStop(armWithMachine));
}
else
{
    Console.WriteLine("qemu-system-aarch64.exe not found in this QEMU install - skipping.");
}

Console.WriteLine("\n--- QMP timeout (ARCHITECTURE_AUDIT.md recommendation #4) ---");
// Proves QmpClient actually gives up instead of hanging forever, against a
// real TcpListener standing in for a hung QEMU - provoking an actual hang
// in real QEMU on demand is not practical here, but the timeout logic only
// cares that something accepted the TCP connection and then never answers,
// which this reproduces exactly.
{
    var neverGreetsListener = new TcpListener(IPAddress.Loopback, 0);
    neverGreetsListener.Start();
    var neverGreetsPort = ((IPEndPoint)neverGreetsListener.LocalEndpoint).Port;
    _ = neverGreetsListener.AcceptTcpClientAsync(); // accept the connection, then do nothing at all - no greeting, ever

    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await using var qmp = new QmpClient();
        await qmp.ConnectAsync(neverGreetsPort, TimeSpan.FromSeconds(1));
        Console.WriteLine("ConnectAsync unexpectedly succeeded against a listener that never greets.");
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"ConnectAsync correctly timed out after {sw.Elapsed.TotalSeconds:0.0}s: {ex.Message}");
    }
    neverGreetsListener.Stop();
}
{
    // Completes the real handshake normally, then never answers the next
    // command - proves the timeout applies per-command, not just at connect.
    var halfAliveListener = new TcpListener(IPAddress.Loopback, 0);
    halfAliveListener.Start();
    var halfAlivePort = ((IPEndPoint)halfAliveListener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var client = await halfAliveListener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await writer.WriteLineAsync("{\"QMP\": {\"version\": {}, \"capabilities\": []}}");
        await reader.ReadLineAsync(); // the qmp_capabilities request
        await writer.WriteLineAsync("{\"return\": {}}");
        await reader.ReadLineAsync(); // the next command - read it, then never answer
        // Keep the connection open (not merely accepted-then-closed) well
        // past the client's own 1-second timeout below, so the client sees
        // a real hang - no data, connection still open - not an early EOF.
        await Task.Delay(TimeSpan.FromSeconds(5));
    });

    var sw2 = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await using var qmp = new QmpClient();
        await qmp.ConnectAsync(halfAlivePort);
        Console.WriteLine("Connect + capabilities handshake against the fake server succeeded.");
        await qmp.ExecuteAsync("query-status", timeout: TimeSpan.FromSeconds(1));
        Console.WriteLine("ExecuteAsync unexpectedly succeeded against a server that never answers.");
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"ExecuteAsync correctly timed out after {sw2.Elapsed.TotalSeconds:0.0}s: {ex.Message}");
    }
    halfAliveListener.Stop();
}

Console.WriteLine("\n--- VNC headless display mode ---");
// Proves Display == "vnc" actually starts a real VNC server reachable on
// the reported port, not just that GetVncEndpoint returns a plausible-
// looking string. A real VNC server's very first move on any new TCP
// connection, before the client sends anything, is "RFB <version>\n" -
// reading that back is a much stronger check than "the port accepted a
// connection" (lots of things would).
{
    var vncVm = new VmConfig { Name = "selftest-vnc", Architecture = "x86_64", MemoryMB = 256, CpuCores = 1, Display = "vnc" };
    var vncManager = new VmProcessManager();
    Console.WriteLine(await vncManager.StartAsync(vncVm, qemuDir));

    var endpoint = vncManager.GetVncEndpoint(vncVm.Id);
    Console.WriteLine($"GetVncEndpoint: {endpoint}");
    if (endpoint == null)
    {
        Console.WriteLine("No VNC endpoint reported - skipping the protocol check.");
    }
    else
    {
        var parts = endpoint.Split(':');
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(parts[0], int.Parse(parts[1]));
        var buffer = new byte[12];
        var read = await tcp.GetStream().ReadAsync(buffer, 0, buffer.Length);
        var banner = Encoding.ASCII.GetString(buffer, 0, read).TrimEnd('\0', '\n', '\r');
        Console.WriteLine($"Raw bytes read from that port: \"{banner}\" (a real VNC server always opens with \"RFB <version>\")");
    }

    Console.WriteLine(vncManager.ForceStop(vncVm));
    await Task.Delay(300);
    Console.WriteLine($"State after force stop: {vncManager.GetState(vncVm.Id)}");
}

Console.WriteLine("\n--- Disk image creation (qemu-img) ---");
var diskPath = Path.Combine(Path.GetTempPath(), "qemumanager-selftest.qcow2");
if (File.Exists(diskPath)) File.Delete(diskPath);
var diskMsg = await DiskImageService.CreateQcow2Async(qemuDir, diskPath, 1);
Console.WriteLine(diskMsg);
Console.WriteLine($"File exists: {File.Exists(diskPath)}, size: {(File.Exists(diskPath) ? new FileInfo(diskPath).Length : 0)} bytes");
if (File.Exists(diskPath)) File.Delete(diskPath);

Console.WriteLine("\n--- VmService facade (Core architecture refactor) ---");
// Proves the one facade MainWindow.xaml.cs now talks to for everything
// VM-related - no VmStore, no per-engine manager - actually dispatches
// correctly to whichever engine a VmConfig.Kind names, end to end against
// the real binaries, not just that VmService compiles. Every assertion
// here is something a window would otherwise be the only way to observe.
{
    IVmService service = new VmService();

    var riscvDraft = service.CreateDraft("RiscvEmu");
    var qemuDraft = service.CreateDraft("Qemu");
    Console.WriteLine($"CreateDraft(\"RiscvEmu\").MemoryMB == 512: {riscvDraft.MemoryMB == 512} (actual: {riscvDraft.MemoryMB})");
    Console.WriteLine($"CreateDraft(\"Qemu\").MemoryMB unaffected (VmConfig's own default): {qemuDraft.MemoryMB}");

    Console.WriteLine("\n--- VM list management via the facade (LoadAll/Save/Delete - no VmStore in MainWindow any more) ---");
    {
        var before = service.LoadAll().Count;
        var draft = service.CreateDraft("RiscvEmu");
        draft.Name = "selftest-facade-crud";
        service.Save(draft);
        var afterSave = service.LoadAll();
        Console.WriteLine($"LoadAll count after Save: {afterSave.Count} (expected: {before + 1}), contains the new VM: {afterSave.Any(v => v.Id == draft.Id)}");
        service.Delete(draft);
        var afterDelete = service.LoadAll();
        Console.WriteLine($"LoadAll count after Delete: {afterDelete.Count} (expected: {before}), contains the deleted VM: {afterDelete.Any(v => v.Id == draft.Id)} (expected: False)");
    }

    var facadeKernelPath = Path.Combine(riscvEmuDir, "boot", "KernelImage");
    if (RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvEmuDir) && File.Exists(facadeKernelPath))
    {
        var facadeVmRiscv = new VmConfig { Kind = "RiscvEmu", Name = "selftest-engine-facade", KernelPath = facadeKernelPath };

        var hw = service.GetHardwareDescription(facadeVmRiscv);
        Console.WriteLine($"GetHardwareDescription (riscv-emu): Engine={hw.EngineDisplayName}, Architecture={hw.Architecture}, CPU={hw.CpuDescription}, Accelerator={hw.AcceleratorDescription}");
        Console.WriteLine($"  EngineDisplayName == riscv-emu: {hw.EngineDisplayName == "riscv-emu"}, Architecture == riscv64: {hw.Architecture == "riscv64"}, CPU mentions 1 hart: {hw.CpuDescription.Contains("1 hart")}, Accelerator mentions Interpreter: {hw.AcceleratorDescription.Contains("Interpreter")}");

        Console.WriteLine($"GetConsoleAvailability before start: {service.GetConsoleAvailability(facadeVmRiscv).kind} (expected: NotRunning)");

        VmStats? lastStats = null;
        service.StatsReceived += (vmId, s) => { if (vmId == facadeVmRiscv.Id) lastStats = s; };

        var settings = new AppSettings { QemuDir = qemuDir, RiscvEmuDir = riscvEmuDir };
        Console.WriteLine(await service.StartAsync(facadeVmRiscv, settings));
        Console.WriteLine($"GetState: {service.GetState(facadeVmRiscv)} (expected: Running)");
        Console.WriteLine($"GetConsoleAvailability while running: {service.GetConsoleAvailability(facadeVmRiscv).kind} (expected: Stream - riscv-emu has no other display)");

        // VmService's own stats timer ticks every 2s - 3s guarantees at
        // least one tick, and riscv-emu's own stderr stats line (tied to
        // instruction count, not wall clock - thousands of them land
        // before the first tick at any real emulation speed) guarantees
        // Mips/Instructions are already cached by the time it fires.
        await Task.Delay(3000);
        Console.WriteLine(lastStats is { } s1
            ? $"StatsReceived for riscv-emu: state={s1.State}, cpu={s1.CpuPercent:0.#}%, mem={s1.MemoryBytes / 1024.0 / 1024.0:0.#} MB, uptime={s1.Uptime}, mips={s1.Mips:0.#}, instructions={s1.Instructions} (expect Mips/Instructions non-null)"
            : "StatsReceived never fired for riscv-emu (unexpected)");

        // ShutdownAsync for riscv-emu is still ForceStop under the hood
        // (see VmService's own remarks) - same real-process-exits proof the
        // dedicated RiscvEmu engine test earlier already gives ForceStop
        // directly, now exercised through the facade instead.
        Console.WriteLine(await service.ShutdownAsync(facadeVmRiscv));
        await Task.Delay(300);
        Console.WriteLine($"GetState after ShutdownAsync: {service.GetState(facadeVmRiscv)} (expected: Stopped)");
    }
    else
    {
        Console.WriteLine(RiscvEmuLocator.LooksLikeRiscvEmuDir(riscvEmuDir)
            ? $"No kernel Image found at '{facadeKernelPath}' - skipping the riscv-emu half of the facade test."
            : "riscv-emu not found - skipping the riscv-emu half of the facade test.");
    }

    if (QemuLocator.LooksLikeQemuDir(qemuDir))
    {
        var facadeVmQemu = new VmConfig { Kind = "Qemu", Name = "selftest-engine-facade-qemu", Architecture = "x86_64", MemoryMB = 128, CpuCores = 1, Display = "none" };

        var hw = service.GetHardwareDescription(facadeVmQemu);
        Console.WriteLine($"GetHardwareDescription (QEMU): Engine={hw.EngineDisplayName}, Architecture={hw.Architecture}, CPU={hw.CpuDescription}, Accelerator={hw.AcceleratorDescription}");
        Console.WriteLine($"  EngineDisplayName == QEMU: {hw.EngineDisplayName == "QEMU"}, Architecture == x86_64: {hw.Architecture == "x86_64"}, CPU mentions core count: {hw.CpuDescription.Contains("1 core")}, Accelerator == tcg: {hw.AcceleratorDescription == "tcg"}");

        VmStats? lastStats = null;
        service.StatsReceived += (vmId, s) => { if (vmId == facadeVmQemu.Id) lastStats = s; };

        var settings = new AppSettings { QemuDir = qemuDir, RiscvEmuDir = riscvEmuDir };
        Console.WriteLine(await service.StartAsync(facadeVmQemu, settings));
        Console.WriteLine($"GetState: {service.GetState(facadeVmQemu)} (expected: Running)");
        Console.WriteLine($"GetConsoleAvailability with Display=none: {service.GetConsoleAvailability(facadeVmQemu).kind} (expected: ExternalWindow)");
        var detail = await service.GetGuestDetailAsync(facadeVmQemu);
        Console.WriteLine($"GetGuestDetailAsync for a real QEMU VM (expected: non-null, e.g. 'running'): {detail ?? "<null - unexpected>"}");

        await Task.Delay(3000);
        Console.WriteLine(lastStats is { } s2
            ? $"StatsReceived for QEMU: state={s2.State}, cpu={s2.CpuPercent:0.#}%, mem={s2.MemoryBytes / 1024.0 / 1024.0:0.#} MB, uptime={s2.Uptime}, guestDetail={s2.GuestDetail}, mips={s2.Mips} (expect Mips null - QEMU has no such channel)"
            : "StatsReceived never fired for QEMU (unexpected)");

        Console.WriteLine(service.ForceStop(facadeVmQemu));
        await Task.Delay(300);
        Console.WriteLine($"GetState after ForceStop: {service.GetState(facadeVmQemu)} (expected: Stopped)");
    }
    else
    {
        Console.WriteLine("QEMU not found - skipping the QEMU half of the facade test.");
    }
}

Console.WriteLine("\n--- Idempotency guards (bug report: double-clicking Console/Power On/Shut Down/Force Stop) ---");
// These two guards are what MainWindow.OpenConsoleWindow and
// StartAsync/ShutdownAsync/ForceStop actually rely on to stay correct
// under a double-click - tested here directly against VmService, with no
// window involved, exactly because a window was never the right place for
// this logic to live or to be tested from.
{
    IVmService guardService = new VmService();

    Console.WriteLine("\n--- Console session guard (TryBeginConsoleSession/EndConsoleSession) ---");
    {
        const string fakeVmId = "selftest-console-guard-vm";
        var first = guardService.TryBeginConsoleSession(fakeVmId);
        var second = guardService.TryBeginConsoleSession(fakeVmId); // simulates the second click of a double-click
        Console.WriteLine($"First TryBeginConsoleSession: {first} (expected: True)");
        Console.WriteLine($"Second TryBeginConsoleSession before EndConsoleSession: {second} (expected: False - this is what stops a second ConsoleWindow from ever being created)");
        guardService.EndConsoleSession(fakeVmId);
        var third = guardService.TryBeginConsoleSession(fakeVmId);
        Console.WriteLine($"TryBeginConsoleSession after EndConsoleSession: {third} (expected: True - a closed console's session was actually released)");
        guardService.EndConsoleSession(fakeVmId);
    }

    Console.WriteLine("\n--- Action-in-progress guard (TryBeginAction/IsActionInProgress, via StartAsync) ---");
    // Deliberately a QEMU VM, not riscv-emu: RiscvEmuProcessManager.Start
    // is fully synchronous (no Task, no await inside it anywhere), so
    // VmService.StartAsync's riscv-emu branch runs start-to-finish -
    // including its own TryBeginAction/EndAction pair - before it ever
    // returns a Task to await, leaving no actual window for a second call
    // to observe as "in progress" (confirmed: tried this with riscv-emu
    // first, and IsActionInProgress read back False immediately, correctly
    // reflecting that the single-threaded synchronous call had already
    // finished, not a guard bug). QEMU's StartAsync genuinely awaits (it
    // polls for its QMP port to come up), which is what gives this test a
    // real window to prove the guard against.
    if (QemuLocator.LooksLikeQemuDir(qemuDir))
    {
        var vmGuard = new VmConfig { Kind = "Qemu", Name = "selftest-action-guard", Architecture = "x86_64", MemoryMB = 128, CpuCores = 1, Display = "none" };
        var settings = new AppSettings { QemuDir = qemuDir, RiscvEmuDir = riscvEmuDir };

        // Fire both "clicks" the way a real double-click would: the second
        // call happens before the first has been awaited at all, not after.
        var firstStart = guardService.StartAsync(vmGuard, settings);
        Console.WriteLine($"IsActionInProgress immediately after the first StartAsync call (before awaiting it): {guardService.IsActionInProgress(vmGuard.Id)} (expected: True)");
        var secondStart = guardService.StartAsync(vmGuard, settings); // the "double click"

        var firstResult = await firstStart;
        var secondResult = await secondStart;
        Console.WriteLine($"First StartAsync result: {firstResult}");
        Console.WriteLine($"Second (overlapping) StartAsync result: {secondResult} (expected: \"Another action is already in progress for this VM.\")");
        Console.WriteLine($"IsActionInProgress after both completed: {guardService.IsActionInProgress(vmGuard.Id)} (expected: False)");
        Console.WriteLine($"GetState: {guardService.GetState(vmGuard)} (expected: Running - exactly one qemu-system-x86_64.exe actually started, not two)");

        Console.WriteLine(guardService.ForceStop(vmGuard));
        await Task.Delay(300);
        Console.WriteLine($"GetState after cleanup: {guardService.GetState(vmGuard)} (expected: Stopped)");
    }
    else
    {
        Console.WriteLine("QEMU not found - skipping the action-in-progress half of this test.");
    }
}

Console.WriteLine("\n--- CrashLogger (global unhandled-exception logging) ---");
{
    var marker = $"selftest-crash-marker-{Guid.NewGuid():N}";
    string path;
    try { throw new InvalidOperationException(marker); } // actually throw/catch it, not just construct it - an Exception that was never thrown has no StackTrace at all, which would make this test pass without ever proving the real "celým stack trace" requirement
    catch (Exception ex) { path = CrashLogger.LogException(ex, "SelfTest"); }

    Console.WriteLine($"LogException returned a path: {!string.IsNullOrEmpty(path)}");
    Console.WriteLine($"File exists: {File.Exists(path)}");
    var contents = File.Exists(path) ? File.ReadAllText(path) : "";
    Console.WriteLine($"Logged file contains the exception message: {contents.Contains(marker)}");
    Console.WriteLine($"Logged file contains a real stack trace: {contents.Contains(" at ")}");
    Console.WriteLine($"LogsDir is AppPaths.AppDataDir's own 'logs' subfolder (so it follows portable mode, and this test's own QEMUMANAGER_DATA_DIR override - never the real %APPDATA% even transiently): {CrashLogger.LogsDir == Path.Combine(AppPaths.AppDataDir, "logs")}");
    if (File.Exists(path)) File.Delete(path);
}

Console.WriteLine("\n--- AppPaths.ResolveAppDataDir (portable vs %APPDATA%, marker-based) ---");
// Exercises every branch against temp directories this test controls, not
// the real AppDataDir property (which is a static field cached the first
// time anything touches it - no way to re-decide it with different inputs
// inside this same process). This is exactly the logic the
// "repro-console-dblclick ended up looking like a real VM" confusion came
// from, so it gets a real test now.
{
    var fakeBaseDir = Path.Combine(Path.GetTempPath(), $"apppaths-test-{Guid.NewGuid():N}");
    var fakeRoaming = Path.Combine(Path.GetTempPath(), $"apppaths-test-roaming-{Guid.NewGuid():N}");
    Directory.CreateDirectory(fakeBaseDir);
    Directory.CreateDirectory(fakeRoaming);
    try
    {
        var noMarker = AppPaths.ResolveAppDataDir(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"No marker, no override -> %APPDATA%\\QemuManager: {noMarker == Path.Combine(fakeRoaming, "QemuManager")} (actual: {noMarker})");

        var overrideResult = AppPaths.ResolveAppDataDir(fakeBaseDir, Path.Combine(Path.GetTempPath(), "apppaths-test-override"), fakeRoaming);
        Console.WriteLine($"Override set -> exactly that directory, no marker check at all: {overrideResult == Path.Combine(Path.GetTempPath(), "apppaths-test-override")}");

        File.WriteAllText(Path.Combine(fakeBaseDir, "portable.flag"), "test marker");
        var withFlag = AppPaths.ResolveAppDataDir(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"portable.flag present -> the base dir's own \"data\" folder: {withFlag == Path.Combine(fakeBaseDir, "data")} (actual: {withFlag})");
        File.Delete(Path.Combine(fakeBaseDir, "portable.flag"));

        Directory.CreateDirectory(Path.Combine(fakeBaseDir, "data"));
        var withExistingDataFolder = AppPaths.ResolveAppDataDir(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"No flag, but \"data\" already exists -> still treated as portable (the grandfather case for an already-portable install): {withExistingDataFolder == Path.Combine(fakeBaseDir, "data")}");
    }
    finally
    {
        try { Directory.Delete(fakeBaseDir, recursive: true); } catch { }
        try { Directory.Delete(fakeRoaming, recursive: true); } catch { }
    }
}

Console.WriteLine("\n--- AppPaths legacy-data import (Import/Start empty, not a silent auto-copy) ---");
// The old behavior copied an entire %APPDATA%\QemuManager directory tree
// automatically, disk images included, the first time a portable build
// ran on a machine that already had one - confirmed directly against a
// real %APPDATA%\QemuManager during this session's own release-zip
// verification (copied a real multi-hundred-MB .qcow2 disk, 4 VM
// configs, settings.json and 2 old crash logs into a scratch folder with
// no prompt at all). This exercises the replacement: detection is
// opt-in information for the caller to act on, not a trigger that acts
// by itself, and the actual copy touches only vms/*.json and
// settings.json - never disks/, logs/, or run/ - leaving each VM's own
// DiskPath exactly as saved (still pointing at the original, uncopied
// file in its old location).
{
    var fakeBaseDir = Path.Combine(Path.GetTempPath(), $"apppaths-import-test-{Guid.NewGuid():N}");
    var fakeRoaming = Path.Combine(Path.GetTempPath(), $"apppaths-import-roaming-{Guid.NewGuid():N}");
    Directory.CreateDirectory(fakeBaseDir);
    var legacyVmsDir = Path.Combine(fakeRoaming, "QemuManager", "vms");
    var legacyDisksDir = Path.Combine(fakeRoaming, "QemuManager", "disks");
    var legacyLogsDir = Path.Combine(fakeRoaming, "QemuManager", "logs");
    Directory.CreateDirectory(legacyVmsDir);
    Directory.CreateDirectory(legacyDisksDir);
    Directory.CreateDirectory(legacyLogsDir);
    try
    {
        // Not portable yet (no marker) - nothing to import regardless of
        // what the "legacy" location has, since there is no portable
        // folder for it to be legacy *relative to*.
        var beforePortable = AppPaths.HasUnimportedLegacyData(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"Not portable (no marker) -> HasUnimportedLegacyData is False: {!beforePortable}");

        File.WriteAllText(Path.Combine(fakeBaseDir, "portable.flag"), "test marker");

        var noLegacyVms = AppPaths.HasUnimportedLegacyData(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"Portable, but legacy vms/ is empty -> HasUnimportedLegacyData is False: {!noLegacyVms}");

        var legacyDiskPath = Path.Combine(legacyDisksDir, "selftest-legacy-disk.qcow2");
        File.WriteAllBytes(legacyDiskPath, new byte[1024]); // stand-in for a real disk image - content doesn't matter, only that it must NOT get copied
        var legacyVmJson = $$"""
        {
          "Id": "selftest-legacy-vm",
          "Name": "Legacy VM",
          "Kind": "Qemu",
          "DiskPath": "{{legacyDiskPath.Replace("\\", "\\\\")}}"
        }
        """;
        File.WriteAllText(Path.Combine(legacyVmsDir, "selftest-legacy-vm.json"), legacyVmJson);
        File.WriteAllText(Path.Combine(fakeRoaming, "QemuManager", "settings.json"), "{\"QemuDir\":\"C:\\\\legacy-qemu\"}");
        File.WriteAllText(Path.Combine(legacyLogsDir, "crash-old.log"), "old crash");

        var hasLegacy = AppPaths.HasUnimportedLegacyData(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"Portable, legacy vms/ has a VM, portable data/ doesn't exist yet -> HasUnimportedLegacyData is True: {hasLegacy}");

        var overrideSet = AppPaths.HasUnimportedLegacyData(fakeBaseDir, "C:\\some\\override", fakeRoaming);
        Console.WriteLine($"QEMUMANAGER_DATA_DIR override set -> never prompts regardless: {!overrideSet}");

        var imported = AppPaths.ImportLegacyConfigs(fakeBaseDir, fakeRoaming);
        Console.WriteLine($"ImportLegacyConfigs returned: {imported} (expected: 1)");

        var portableVmsDir = Path.Combine(fakeBaseDir, "data", "vms");
        var importedVmFile = Path.Combine(portableVmsDir, "selftest-legacy-vm.json");
        Console.WriteLine($"VM config copied to portable data/vms: {File.Exists(importedVmFile)}");

        var importedContents = File.Exists(importedVmFile) ? File.ReadAllText(importedVmFile) : "";
        Console.WriteLine($"Copied VM's own DiskPath still points at the ORIGINAL (uncopied) legacy file: {importedContents.Contains(legacyDiskPath.Replace("\\", "\\\\"))}");

        Console.WriteLine($"settings.json copied: {File.Exists(Path.Combine(fakeBaseDir, "data", "settings.json"))}");

        var disksCopied = Directory.Exists(Path.Combine(fakeBaseDir, "data", "disks"));
        Console.WriteLine($"disks/ NOT copied into the portable folder: {!disksCopied}");
        var logsCopied = Directory.Exists(Path.Combine(fakeBaseDir, "data", "logs"));
        Console.WriteLine($"logs/ NOT copied into the portable folder: {!logsCopied}");

        // The portable data/ folder now exists, so this would no longer
        // offer to import even if asked again - the same "don't ask
        // twice" property the old automatic-copy-on-first-run had.
        var afterImport = AppPaths.HasUnimportedLegacyData(fakeBaseDir, null, fakeRoaming);
        Console.WriteLine($"After import, HasUnimportedLegacyData is False (data/ now exists): {!afterImport}");
    }
    finally
    {
        try { Directory.Delete(fakeBaseDir, recursive: true); } catch { }
        try { Directory.Delete(fakeRoaming, recursive: true); } catch { }
    }
}

Console.WriteLine("\n--- No DataGrid in the app is editable unless explicitly intended (bug: double-click on a cell crashed the app) ---");
// A static check of the actual .xaml source, not a runtime/window check -
// in keeping with "logic belongs in Core, tested without a window" even
// though this is specifically about a WPF concern: the point is that
// *something* keeps checking this automatically from now on, in exactly
// the place a regression would actually show up (a XAML file gaining a
// new DataGrid, or this one losing its default), not that it has to run
// inside a live Window to be meaningful.
{
    var wpfProjectDir = args.Length > 2 ? args[2] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "QemuManager"));
    if (!Directory.Exists(wpfProjectDir))
    {
        Console.WriteLine($"WPF project directory not found at '{wpfProjectDir}' - pass it as a third argument. Skipping.");
    }
    else
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var appXamlPath = Path.Combine(wpfProjectDir, "App.xaml");
        if (File.Exists(appXamlPath))
        {
            var appXaml = XDocument.Load(appXamlPath);
            var dataGridStyle = appXaml.Descendants(xaml + "Style")
                .FirstOrDefault(s => (string?)s.Attribute("TargetType") == "DataGrid");
            var isReadOnlySetter = dataGridStyle?.Descendants(xaml + "Setter")
                .FirstOrDefault(s => (string?)s.Attribute("Property") == "IsReadOnly");
            Console.WriteLine($"App.xaml's default DataGrid style exists: {dataGridStyle != null}");
            Console.WriteLine($"  ...and sets IsReadOnly=\"True\": {(string?)isReadOnlySetter?.Attribute("Value") == "True"}");
        }
        else
        {
            Console.WriteLine($"App.xaml not found under '{wpfProjectDir}' - skipping the default-style check.");
        }

        var offenders = new List<string>();
        var gridCount = 0;
        foreach (var xamlFile in Directory.EnumerateFiles(wpfProjectDir, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument doc;
            try { doc = XDocument.Load(xamlFile); }
            catch { continue; } // not well-formed XML (shouldn't happen for a real .xaml) - not this check's job to report that
            foreach (var grid in doc.Descendants(xaml + "DataGrid"))
            {
                gridCount++;
                var isReadOnlyAttr = (string?)grid.Attribute("IsReadOnly");
                if (isReadOnlyAttr == "False")
                    offenders.Add($"{Path.GetFileName(xamlFile)}: a <DataGrid> explicitly overrides IsReadOnly=\"False\" (editable) - intended, or a regression?");
            }
        }
        Console.WriteLine($"DataGrid elements found across all .xaml files: {gridCount} (expected: at least 1 - MainWindow.xaml's VmList)");
        if (offenders.Count == 0)
        {
            Console.WriteLine("No DataGrid explicitly overrides IsReadOnly=\"False\" - every one of them is read-only via App.xaml's own default.");
        }
        else
        {
            foreach (var o in offenders) Console.WriteLine($"NOTE: {o}");
        }
    }
}

Console.WriteLine("\n--- VM tree nodes have a real accessible name, not ToString() (Phase 3 detail-page work) ---");
// Found while verifying the new VM detail page via UI Automation: without
// an explicit AutomationProperties.Name on the generated TreeViewItem
// containers, each VM's node read as "QemuManager.Models.VmListItem" to a
// screen reader or any other UI Automation client - the same class of bug
// as the DataGrid's stray DataGridDetailsPresenter (see the check above),
// just audible/inspectable rather than visible on screen, so a plain
// visual check would never have caught it. Static, for the same reason as
// the DataGrid check above - the point is that something keeps checking
// this automatically, not that it needs a live window to be meaningful.
{
    var wpfProjectDir = args.Length > 2 ? args[2] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "QemuManager"));
    var mainWindowXamlPath = Path.Combine(wpfProjectDir, "MainWindow.xaml");
    if (!File.Exists(mainWindowXamlPath))
    {
        Console.WriteLine($"MainWindow.xaml not found under '{wpfProjectDir}' - skipping.");
    }
    else
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var doc = XDocument.Load(mainWindowXamlPath);
        var vmNode = doc.Descendants(xaml + "TreeViewItem")
            .FirstOrDefault(t => (string?)t.Attribute("Tag") == "VirtualMachines");
        var containerStyle = vmNode?.Descendants(xaml + "Style")
            .FirstOrDefault(s => (string?)s.Attribute("TargetType") == "TreeViewItem");
        var nameSetter = containerStyle?.Descendants(xaml + "Setter")
            .FirstOrDefault(s => (string?)s.Attribute("Property") == "AutomationProperties.Name");
        Console.WriteLine($"VirtualMachinesNode found: {vmNode != null}");
        Console.WriteLine($"Its ItemContainerStyle sets AutomationProperties.Name: {nameSetter != null} (actual value: {(string?)nameSetter?.Attribute("Value")})");
    }
}

Console.WriteLine("\nDone.");

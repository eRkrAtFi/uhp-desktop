# QEMU Manager

A standalone Windows desktop app (.NET 8 / WPF) that uses QEMU as its
virtualization core: it never emulates anything itself, it builds QEMU's own
command line, launches `qemu-system-<arch>.exe`, and drives it over QEMU's own
management protocol, QMP. This is the opposite relationship from `emu86/` next
door - that project studies QEMU's internals by reimplementing a slice of them;
this one treats QEMU as a finished engine and builds a front end for it, the
way virt-manager or the GNOME Boxes do on Linux.

Each Environment has an `EngineId`: "Qemu" (above) or "RiscvEmu" - a
frontend for this repo's *own* hand-written emulator, `riscv-emu/`, so it
can be run the normal way (double-click an icon, Start/Stop, a console
window) instead of only from a terminal with a long flag list. See
"riscv-emu VMs" below for how that differs from the QEMU path. (This used
to be a plain string field called `Kind` on a flat `VmConfig` - the
current shape, `EnvironmentConfig.EngineId` plus `IEngine`, is what the
"Project layout" section below describes; VM/Environment and Kind/EngineId
are used interchangeably in the rest of this older document.)

The main window is a sidebar (Host/Environments/Storage/Networking/
Engines) plus, for Environments, a grid: each row shows a status dot
(green/amber/gray), the Environment's engine and architecture, and
**live** CPU%/memory - real numbers read
straight from the actual `qemu-system-*.exe`/`riscv-emu.exe` process via
`Process.TotalProcessorTime`/`WorkingSet64` (`VmProcessManager`/
`RiscvEmuProcessManager.GetResourceUsage`), not anything the guest
self-reports and not the VM's configured RAM allocation - polled every 2
seconds alongside the existing QMP guest-status check.

## What it does

- **VM list** - every VM is a small JSON file under this app's own
  portable `data\vms\` folder next to the `.exe` (see "Portable by design"
  below), loaded on startup.
- **Create / edit** - name, architecture (read from whichever
  `qemu-system-*.exe` binaries are actually present), memory, CPU cores,
  accelerator (`tcg` always available, `whpx` if Hyper-V Platform is on),
  disk image, CD-ROM/ISO, network mode, display backend, boot order, free-form
  extra arguments.
- **Disk creation** - a "New..." button next to the disk field (which
  opens the same dialog the Storage page's own "Create disk" does) picks
  raw (written directly, no QEMU needed) or qcow2 (`qemu-img.exe create`),
  never overwrites an existing file, and records every disk it makes in a
  small registry so it stays visible on the Storage page even after its
  own Environment is later deleted.
- **Start** - builds the argument list and starts the process with the
  windowed-subsystem build (`qemu-system-<arch>w.exe`) so no console window
  flashes behind the app. With Display set to `gtk`/`sdl`, QEMU opens its
  own display window as usual; with `console`, there is no QEMU window at
  all - `-serial stdio` puts the guest's virtual COM1 on this process's own
  stdio, piped into a `ConsoleWindow` the same way riscv-emu VMs already
  work (see below) - a real embedded text console, not a second window to
  manage. Confirmed end-to-end booting TinyCore Linux
  (`-kernel`/`-initrd`/`-append "console=ttyS0"` via Extra arguments) all
  the way to a root shell, entirely inside this app's own window.
- **Stop (ACPI)** - connects over QMP (`-qmp tcp:127.0.0.1:<port>,server,nowait`)
  and sends `system_powerdown`: the same signal a power button sends. The
  guest OS decides whether and when to actually shut down.
- **Force Stop** - kills the process tree. Equivalent to pulling the power
  cord; always available as a fallback.
- **Live status** - polls `query-status` over QMP every few seconds, so the
  grid shows "Running (running)" vs "Running (paused)" etc., not just "the
  process hasn't exited yet."

## riscv-emu VMs

> **Historical note:** this section (and "Two real differences from the
> QEMU path" below it) describes riscv-emu as it was when this was
> written - mid-development, not yet reaching a shell. None of that is
> true any more: riscv-emu now boots a real kernel to an interactive
> shell, has a graceful Shut Down (`Workload.ShutdownCommand`, typed at
> the console, the same as any other Environment's own Edit window
> shows), virtio-blk disk and virtio-net NAT (see the root
> [`README.md`](../README.md)), and a bare-metal ELF workload with a GDB
> stub (roadmap item 3 - see [`vision/VISION.md`](../vision/VISION.md)).
> It is still a single-hart machine with no SMP, and memory is
> configurable (`Platform.MemoryMB`, defaulting to 512 MB) rather than
> truly fixed. The mechanics described below (`RiscvEmuBoot`,
> `DtbBuilder`, `RiscvEmuProcessManager`) are still how the
> `LinuxKernel` workload boots today.

A "New Environment" now asks which kind to create. For "riscv-emu" the form is much
shorter - a name, a kernel `Image` path, an optional initramfs, and extra
kernel boot-command-line arguments - because there is no QMP or display
backend choice, and CPU is fixed at one hart (riscv-emu has no SMP today).
Starting one:

1. `Services/RiscvEmuBoot.cs` builds the same command line
   `riscv-emu/boot/boot.sh` does, but in C# rather than shelling out to it -
   reads the kernel Image header's `text_offset` directly, places the
   initramfs and a generated DTB past the kernel with margin (computed from
   the actual file sizes, not hardcoded addresses), and sets `a0`/`a1`/`t3`
   the way `riscv-emu/boot/firmware.S` expects.
2. `Services/DtbBuilder.cs` is a C# port of `riscv-emu/boot/make_dtb.py`'s
   FDT-format builder - this app needs neither Python nor `dtc` installed to
   run, only `riscv-emu.exe` and its prebuilt `boot/firmware.bin`.
3. `Services/RiscvEmuProcessManager.cs` starts the process with its stdout
   and stderr redirected and read as raw bytes (not a text `StreamReader` -
   the guest's own console output is not guaranteed to be valid UTF-8), and
   a `ConsoleWindow` shows it live, auto-opened on Start.

One real difference from the QEMU path remains, not an oversight:
**no ACPI-style graceful stop.** Real QEMU has QMP's `system_powerdown` (a
signal the guest OS decides whether to honor). riscv-emu has no equivalent
control-channel signal - its own graceful Shut Down instead types
`Workload.ShutdownCommand` (default `poweroff`) at the console and waits
for the process to exit on its own, same as typing it by hand would; Force
Stop still just terminates the process either way. Console input itself is
fully interactive today (see `vision/NEXT.md` and the root README's own
Status for what else changed since this section was first written).

## QEMU VMs with Display = "console"

`ConsoleWindow` isn't riscv-emu-specific: it takes an `IConsoleSource`
(`Services/IConsoleSource.cs` - just `OutputReceived`, `SendInput`,
`ForceStop`), which both `RiscvEmuProcessManager` and `VmProcessManager`
implement. For a real QEMU VM, `VmProcessManager` only redirects stdio and
raises `OutputReceived` when that VM's `Display` is `"console"` - `gtk`/
`sdl` VMs are untouched, QEMU still opens its own window for those exactly
as before. Console mode's own argument building swaps `-display <gtk|sdl>`
for `-display none -serial stdio -monitor none` (QMP, on its own TCP port,
remains the control channel either way - nothing about Stop/Force
Stop/live status changes).

Unlike riscv-emu, input genuinely works here: confirmed by booting TinyCore
Linux (`CorePure64.iso`'s extracted `vmlinuz64`/`corepure64_serial.gz`, via
`-kernel`/`-initrd`/`-append "console=ttyS0"` in Extra arguments - a real
ISO boot would need `console=ttyS0` reachable some other way, e.g. editing
the isolinux prompt, since most live-CD bootloaders default to a VGA-only
console) all the way to a root shell, entirely inside this app's own
`ConsoleWindow`, by actually typing into it and running real commands.

Two real bugs surfaced while getting that far, neither specific to this
app's own code - both confirmed first with a plain `qemu-system-x86_64.exe`
test, no GUI involved:

- **`console=ttyS0` only redirects kernel log *output*, not where init
  spawns a login shell.** TinyCore's own `/etc/inittab` (baked into its
  initramfs) only has a `getty` on `tty1` - the VGA console, invisible
  with `-display none`. Typed input was reaching a real tty (confirmed by
  its own kernel-level echo), just not one anything was reading, so lines
  never "finished" and each next command landed on the end of the still-
  open previous one. Fixed outside this repo, by editing TinyCore's own
  config: unpacked `corepure64.gz` (newc-format cpio), added
  `ttyS0::respawn:/sbin/getty -nl /sbin/autologin 115200 ttyS0` to
  `/etc/inittab`, repacked as `testimages/corepure64_serial.gz` - the file
  this repo's own TinyCore VM config now points at.
- **Sending a whole typed line as one bulk write can silently drop bytes
  in the middle of it** (confirmed: `echo HELLO123` arrived as
  `echo HEL3` sent as a single write, byte-for-byte correct sent one byte
  at a time with a few milliseconds between each) - a real human typing
  the same line one keystroke at a time never hits this, but this app's
  own "type a line, hit Send" box effectively always bursts it. Fixed in
  `VmProcessManager.SendInput`/`RiscvEmuProcessManager.SendInput`: both
  now pace output a byte at a time with a small delay, on a background
  task so a long paste cannot freeze the UI thread for the pacing delay.
- Also: a real serial terminal's convention of sending CR (`\r`) for
  Enter turned out not to apply here - this guest's tty line discipline
  never treated `\r` as end-of-line at all (every send just landed on the
  same open line), while plain `\n` did. Confirmed since as a console-
  level ICRNL difference, not something specific to this one image: the
  project's own riscv-emu/QEMU Buildroot guest has ICRNL on (`stty -a`),
  so the terminal's default of sending plain CR already works there with
  no help needed, while TinyCore's own console (set up above) does not.
  Each VM's own Edit window now has an "Enter sends" setting (CR/LF/CRLF,
  default CR) for exactly this - TinyCore's VM config sets it to LF.

## Guided machine type / firmware for non-x86_64 architectures

`BuildArguments` used to be entirely architecture-agnostic beyond picking
which `qemu-system-<arch>.exe` to run - fine for x86_64, which boots with
QEMU's own defaults, but several other architectures have *no* usable
default machine type at all and refuse to start without one (confirmed
against a real `qemu-system-aarch64.exe`: with no machine type it exits
within the 6-second QMP-readiness window every time; with `-M virt` it
starts and accepts QMP connections normally - see
`QemuManager.SelfTest`'s "Machine type" section). This was
`ARCHITECTURE_AUDIT.md`'s recommendation #3.

Two new, optional `Platform`/`Firmware` fields close that gap: `MachineType`
(QEMU's `-M`) and `FirmwarePath` (QEMU's `-bios`) - both empty by default, so
every Environment saved before these fields existed behaves exactly as
before. Firmware is never guessed - it is a real file only the user can
supply.

> **Known gap:** the old per-engine `VmEditWindow` used to show a short hint
> for architectures known to need an explicit machine type (aarch64, arm,
> riscv64, riscv32) and offer to fill in the suggested one - lost when it was
> replaced by the schema-driven `EnvironmentEditWindow` (vision/VISION.md's
> roadmap item 2) and not yet ported to the new schema; set `MachineType`
> manually for those architectures until this comes back.

## QMP timeouts

`QmpClient.ExecuteAsync`'s read loop used to have no timeout at all - a
QEMU that accepted the QMP TCP connection but then hung (or anything else
standing in that spot that never actually spoke QMP) left every caller -
Stop (ACPI), live status polling, the initial capabilities handshake -
awaiting a `ReadLineAsync` that would never complete
(`ARCHITECTURE_AUDIT.md` recommendation #4). Every QMP round trip -
connecting, the greeting, the capabilities handshake, and every later
command - now gives up with a `TimeoutException` after 8 seconds by default
(matching `VmProcessManager`'s existing 8-second ACPI-revert window, rather
than inventing an unrelated number), instead of waiting forever.

Verified in `QemuManager.SelfTest` against two real `TcpListener`s standing
in for a hung QEMU - not mocked at the `QmpClient` level, an actual socket
that accepts a connection and then withholds data: one that never sends
the initial `{"QMP": ...}` greeting at all (proves `ConnectAsync` itself
times out), and one that completes a normal handshake and then never
answers a subsequent command (proves the timeout applies per-command, not
only at connect).

## Surviving an app restart

Starting a VM launches a plain OS process with no Job Object tying its
lifetime to this app's - so if `QemuManager.exe` itself is killed, crashes,
or is updated while a VM is running, the real `qemu-system-*.exe`/
`riscv-emu.exe` process keeps running, detached. Without anything to
re-discover it, the next launch would show that VM as "Stopped" while it is
actually still running - not just an unknown status, an *actively wrong*
one (see `ARCHITECTURE_AUDIT.md`'s Critical Risk #1).

Each `*ProcessManager.StartAsync`/`.Start` now also writes a small sidecar
file (`Services/RuntimeStateStore.cs`, under `data\run\` next to the `.exe`) -
just the PID, the OS process name (to detect PID reuse), and for a real
QEMU VM its QMP port - deleted the moment that VM's process actually exits,
whatever the cause (graceful, force-stopped, or the guest halting itself).
On every `LoadVms()` (app startup and "Refresh"), each VM calls
`TryReattach`: if its sidecar file points at a PID that is still alive and
still looks like the right binary, the VM is re-adopted as Running instead
of reported Stopped - confirmed to actually happen in `QemuManager.SelfTest`
by starting a VM with one `VmProcessManager` instance, handing it to a
second, freshly-constructed one (standing in for "the app restarted"), and
confirming that instance can query it over QMP and stop it, neither of
which it could do from empty in-memory state alone.

One real limitation, not an oversight: a reattached VM has no stdio pipes -
those belonged to the OS process handle the original app instance held,
which is gone. `ConsoleMode` is always `false` for a reattached VM even if
its `Display` is `"console"`, so console input/output stay inert until it
is stopped and started again by this app. Stop, Force Stop, and live QMP
status are unaffected - those only need the process handle and (for a real
QEMU VM) the QMP port, both of which are reattached correctly.

## Closing the audit's remaining low-priority items

`ARCHITECTURE_AUDIT.md`'s three LOW-priority recommendations (#5-#7) are
also done now - unlike #1-#4 above, none of them were real bugs by
themselves, but fixing them surfaced one:

- **#5**: `QemuManager.SelfTest` used to only exercise the QEMU engine -
  `RiscvEmuProcessManager`/`RiscvEmuBoot`/`DtbBuilder` had zero automated
  coverage, only ever having been run through the GUI by hand. It now
  starts a real `riscv-emu.exe` VM, samples its resource usage, reattaches
  to it from a second, freshly-constructed `RiscvEmuProcessManager`
  (mirroring the QEMU engine's own reattach test), and force-stops it -
  the same shape of coverage the QEMU engine already had.
- **#7**: `VmProcessManager.GetResourceUsage` and
  `RiscvEmuProcessManager.GetResourceUsage` had near-identical CPU%/memory
  sampling logic; factored into `Services/ResourceUsageSampler.cs`.
- **A real bug this surfaced**: writing that #5 reattach test caught a
  genuine bug in #2's `TryReattach` (both engines) that nothing before
  this had exercised - `RunningVm`/`RunningRiscvEmu.LastCpuTime` defaults
  to `TimeSpan.Zero`, correct for a process this app just started (which
  really has accumulated ~0 CPU time so far) but wrong for one being
  *reattached*, which has already been running for a while. The first
  `GetResourceUsage` call after a reattach was dividing that process's
  entire CPU history by the few milliseconds since reattach, producing a
  nonsense reading - confirmed in `QemuManager.SelfTest` as a 90000%+ CPU
  figure before the fix seeded `LastCpuTime` from the process's actual
  `TotalProcessorTime` at reattach time instead of leaving it at zero.
- **#6**: `VmProcessManager.StartAsync` had a no-op
  `connected ? VmRunState.Running : VmRunState.Running` ternary; replaced
  with a plain assignment and a comment explaining why both branches were
  always meant to be the same (QMP connectivity is not proof of guest
  health), rather than leaving it looking like a leftover bug.

## VNC: a graphical display that survives closing its window

`Display == "console"` already decouples a VM's console from any one
window - closing `ConsoleWindow` never stops the VM, since it is just a
viewer onto piped stdio. QEMU's own gtk/sdl window has no such separation:
it *is* the VM, as far as QEMU is concerned, so closing it is QEMU's own
built-in "quit" signal - confirmed directly against real QEMU rather than
assumed, including that `-display gtk,window-close=off` is not a fix for
this: it disables the window's close button entirely (the window cannot be
closed that way at all, proven by sending the exact `WM_SYSCOMMAND`/
`SC_CLOSE` message a real click sends and confirming the window and
process were both still there afterward), not "close the window but keep
the VM running."

`Display == "vnc"` gets the same decoupling console mode already has, for
a graphical display: `VmProcessManager.BuildArguments` passes
`-display none -vnc 127.0.0.1:<port - 5900>` (QEMU's VNC port numbering is
an offset from 5900, not a literal port - the port itself still comes from
the same collision-free `GetFreeTcpPort()` QMP already uses) instead of
opening gtk/sdl at all. There is no window for QEMU to treat a close as a
quit signal for, so there is nothing to decouple from in the first place.
The endpoint is shown once in the Start message and again on demand via
Console (`VmProcessManager.GetVncEndpoint`) - connect with any VNC client;
this app has no built-in VNC viewer yet, deliberately: embedding one (the
graphical equivalent of `ConsoleWindow`) is real protocol/rendering work,
not attempted as part of this smaller, scoped change.

Verified end to end in `QemuManager.SelfTest` against real QEMU: started a
VM with `Display = "vnc"`, connected a raw `TcpClient` to the reported
endpoint, and read back `"RFB 003.008"` - a real VNC server's mandatory
first move on any new connection, not just confirmation that something
accepted a TCP connection.

## Why QMP, not just Process.Kill

A GUI that only starts and kills processes isn't really driving QEMU, it's
driving Windows. QMP (`Services/QmpClient.cs`) is QEMU's own JSON-over-TCP
control channel - the same one virt-manager and libvirt use - so this app can
ask a real question ("is the guest actually running, or did it crash?") and
request a graceful shutdown, not just send SIGKILL and hope.

## Project layout

```
QemuManager/
  Models/EnvironmentConfig.cs one Environment's settings (the persisted
                               root shape: Id/Name/EngineId + Platform/
                               Firmware/Workload/DebugConfiguration)
  Models/VmListItem.cs        binds EnvironmentConfig to the main grid with live status
  Services/QemuLocator.cs     finds qemu-system-*.exe / qemu-img.exe
  Services/VmProcessManager.cs   IEngine for QEMU - builds the command line,
                               starts/stops, QMP status, and (Display == "console")
                               pipes stdio like riscv-emu does
  Services/QmpClient.cs       the QMP protocol client itself
  Services/DiskImageService.cs  wraps qemu-img.exe
  Services/EnvironmentStore.cs   JSON persistence, with transparent migration
                               from the old flat VmConfig shape (see EnvironmentMigration.cs)
  Services/IEngine.cs / EngineRegistry.cs   the engine interface both process
                               managers implement, and the one place they are registered
  Services/IConsoleSource.cs     what ConsoleWindow/TerminalControl need from either engine
  Services/RiscvEmuLocator.cs    finds riscv-emu.exe / boot/firmware.bin
  Services/RiscvEmuBoot.cs       builds riscv-emu.exe's command line (boot.sh, in C#)
  Services/DtbBuilder.cs         FDT/DTB builder (make_dtb.py, in C#)
  Services/RiscvEmuProcessManager.cs   IEngine for riscv-emu - starts/stops
                               riscv-emu.exe, streams its console
  Services/RuntimeStateStore.cs   sidecar "is this Environment's process still
                               alive" file per running Environment - see
                               "Surviving an app restart" below
  Services/ResourceUsageSampler.cs   the CPU%/memory sampling arithmetic both engines'
                               GetResourceUsage need, factored out once rather
                               than kept duplicated
  MainWindow.xaml(.cs)        Environment list + toolbar
  NewEnvironmentWindow.xaml(.cs)   "Operating System, Kernel / Bootloader,
                               or Bare-metal / Embedded?" template picker
                               shown by New Environment
  EnvironmentEditWindow.xaml(.cs)   one schema-driven create/edit form for
                               either engine (see IEngine.GetConfigSchema)
  ConsoleWindow.xaml(.cs)     live terminal for a running Environment of either
                               kind (riscv-emu always; a Qemu one only if
                               Display == "console")
  TerminalControl.xaml(.cs) / Assets/terminal.html   the real terminal (xterm.js
                               in WebView2) ConsoleWindow and the detail page's
                               own console preview both share
  SettingsWindow.xaml(.cs)    QEMU + riscv-emu install directory pickers
  CreateDiskWindow.xaml(.cs)  new qcow2 disk dialog
QemuManager.SelfTest/         console harness that drives the same Services
                               layer against real QEMU - see below
```

## Portable by design - opt-in via a marker, not inferred from where the .exe runs

`Services/AppPaths.cs` resolves its "data" folder (VM configs, settings,
disks created through the app, runtime-state sidecars) one of two ways,
decided by `AppPaths.ResolveAppDataDir`:

- **Portable**: next to the `.exe` itself (`AppContext.BaseDirectory` -
  not `Assembly.Location`, which has no real value for a
  `PublishSingleFile` build), in a `data` folder. Copying `QemuManager.exe`
  plus that `data` folder to a USB stick or a different machine carries
  every VM along with it; QEMU and riscv-emu remain external dependencies
  either way (point Settings at them on the new machine) unless you also
  copy their folders alongside. Opted into by either a `portable.flag`
  marker file next to the `.exe` (written automatically by
  `package-portable.ps1`), or by a `data` folder already existing there -
  the second case exists so an already-portable install upgrading to this
  check still works without anyone having to retroactively drop a flag
  file into it.
- **Default (no marker)**: `%APPDATA%\QemuManager` - the one real data
  location for every normal run of every build variant (`publish\`,
  `bin\Debug\...`, `bin\Release\...`) that was not explicitly set up as a
  portable bundle.

This replaced an earlier version of this same idea that inferred portable
mode from "does a `data` folder already exist next to whichever `.exe`
just ran" with no marker at all - which meant every freshly-built `.exe`
(a new `bin\Debug` output, a one-off test build, ...) silently cloned
whatever was in `%APPDATA%\QemuManager` into its *own* local `data` folder
the first time it ran and found none there yet. Each clone then diverged
independently with no further syncing - which is how a VM created for a
throwaway repro in a `bin\Debug` build ended up looking like it was "in
the real list": that build's `.exe`, on its own very first run, had
already cloned the real list into a folder that then drifted from it.
`QemuManager.SelfTest` and any other debug/automation run instead set
`QEMUMANAGER_DATA_DIR` to their own temp directory before touching
anything - see that project's `Program.cs` - which overrides this check
outright and never goes near either real location.

The one-time, best-effort migration from this app's pre-portable location
(plain `%APPDATA%\QemuManager`, where every version before "portable by
design" existed kept its data) still runs the first time a portable
bundle's own `data` folder doesn't exist yet - copies, not moves, so
existing VM configs and settings do not appear to vanish just because the
`.exe` is now somewhere else, and the old location is left intact in case
this `.exe` is one of several copies. Confirmed end to end originally: ran
the app once with VMs already saved under the old `%APPDATA%` location,
then with no other change, confirmed `data\vms\*.json`, `data\settings.json`
and even a disk image created earlier through "New..." all appeared next
to the `.exe` automatically, and the running app still showed the same
four VMs as before.

## Fully portable (riscv-emu bundled, QEMU opt-in)

The "Portable by design" section above still left QEMU and riscv-emu as
external dependencies, pointed at through Settings. `AppSettings`'s
`QemuDir`/`RiscvEmuDir` defaults now check for a `qemu`/`riscv-emu` folder
right next to the `.exe` first (`DefaultQemuDir`/`DefaultRiscvEmuDir`,
using the same `QemuLocator`/`RiscvEmuLocator` checks the rest of the app
already relies on), falling back to the previous defaults
(`C:\Program Files\qemu` / unconfigured) only if no such folder exists.
This is only ever the *default value* written into a brand-new
`settings.json` - once one exists (including one migrated from an older
install, see above), whatever it says is authoritative and these defaults
are never consulted again.

`package-portable.ps1` builds the actual bundle: publishes the app, copies
just `riscv-emu.exe` + `boot\firmware.bin` (the entire runtime surface
`RiscvEmuLocator` looks for - not the rest of that project's source, build
tooling, or test logs) into `publish\riscv-emu` always, and - only if
`-QemuDir` is passed explicitly - a full QEMU install (`robocopy /MIR`, a
real install is 1GB+ and ships shared files several `qemu-system-*.exe`
binaries depend on, so this does not try to guess a subset) into
`publish\qemu`. QEMU bundling used to be the default; confirmed directly
against this project's own first few releases that `qemu/` was 88.6% of
the portable zip's total uncompressed size (1256 of 1418 MB) - almost
entirely EDK2 firmware and `qemu-system-*` binaries for architectures this
project's own riscv64 target never touches - so it is opt-in now. A real
QEMU VM still needs a real QEMU install; the main README's own
Requirements section points at [qemu.weilnetz.de](https://qemu.weilnetz.de/w64/)
for that, the same one-time, separately-updatable install any other QEMU
front end would also expect, not a copy with no update path duplicated
into every release of this app. Run it with:

```powershell
.\package-portable.ps1                                    # riscv-emu only (..\riscv-emu) - what release.yml itself builds
.\package-portable.ps1 -QemuDir "C:\Program Files\qemu"    # also bundle a real QEMU install
```

Verified end to end with the legacy `%APPDATA%\QemuManager` location
temporarily moved aside (to rule out it quietly supplying the paths
instead) and the publish folder's own `data` deleted, to force a genuinely
first-ever run: launched `QemuManager.exe` straight from the packaged
`publish` folder with zero prior configuration anywhere, and its status
bar read the bundled `...\publish\riscv-emu` path (and, with `-QemuDir`
passed, `...\publish\qemu (29 architectures)` too) with no Settings
interaction at all - copy the whole `publish` folder anywhere and it
works immediately.

## Build and run

```sh
cd QemuManager/QemuManager
dotnet build                 # Debug build to bin/Debug/.../QemuManager.exe
dotnet publish -c Release    # standalone single-file .exe, no .NET install needed to run it
```

The published `.exe` under `bin/Release/net8.0-windows/win-x64/publish/` is
~155 MB because it bundles the full self-contained .NET + WPF + WinForms
runtime; that is the cost of "no separate install step" rather than a bug.
(`package-portable.ps1` above publishes straight to a `publish\` folder at
this repo's own root instead of that default `bin\...\publish\` path - see
"Fully portable" above.)

On first run it looks for QEMU at `C:\Program Files\qemu`; use Settings to
point it elsewhere if your install is somewhere else. riscv-emu has no
default location - Settings needs pointing at that project's own folder
(the one containing `riscv-emu.exe` and `boot\firmware.bin`) before any
RiscvEmu VM can start.

## Verification

Two layers were actually run against the real `qemu-system-x86_64.exe` in this
checkout, not just compiled:

1. **`QemuManager.SelfTest`** - a console harness that reuses the Services
   files directly (via `<Compile Include>`, no WPF dependency) to start a
   disk-less VM, confirm `query-status` returns `running` over real QMP,
   request an ACPI power-down, force-stop, and create a real qcow2 via
   `qemu-img`. Run it with:
   ```sh
   cd QemuManager.SelfTest
   dotnet run -- "C:\Program Files\qemu"
   ```
2. **A full GUI click-through** - driven with Windows UI Automation
   (`System.Windows.Automation`, run under `pwsh -STA`, since UI Automation's
   COM marshaling needs an STA thread and PowerShell 7 defaults to MTA):
   New Environment → fill in the form → Save → select the row → Start → confirmed the
   real `qemu-system-x86_64w.exe` process was running → read the live status
   bar → Stop (ACPI) → Force Stop → confirmed the process actually exited.
   That script lives in this session's scratchpad, not in the repo, since it
   is a one-off verification tool rather than part of the app; the pattern is
   documented here because it is the reusable part:
   - `AutomationElement.FromHandle(hwnd)` (found via `EnumWindows`), not
     `RootElement.FindAll` by process ID - owned dialog windows did not
     reliably show up under the latter in this environment.
   - `TryGetCurrentPattern`, not `GetCurrentPattern` - the latter threw
     "Unsupported Pattern" for patterns the element demonstrably supported.
   - A DataGrid row found by name can return the matching *cell*, not the
     row; walk up with `TreeWalker.ControlViewWalker` to the `ControlType.DataItem`
     ancestor to get something that supports `SelectionItemPattern`.
   - The native Win32 `MessageBox` (Force Stop's Yes/No confirmation) exposes
     neither `InvokePattern` nor a reachable `LegacyIAccessiblePattern` through
     this bridge; a real `SetCursorPos` + `mouse_event` click on the button's
     `BoundingRectangle` center was the reliable way to press it. Its
     button captions are locale-dependent too (sk-SK: "Áno"/"Nie", not
     "Yes"/"No").

   This caught two real bugs before they shipped: four form fields whose
   `AutomationProperties.Name` was accidentally identical to their label's
   visible text (so `FindFirst` matched the label, not the input - fixed by
   giving each a distinct accessible name), and a "Stop (ACPI)" state that
   never reverted to "Running" when a disk-less guest (predictably) never
   responds to the power button - fixed with an 8-second revert in
   `VmProcessManager`.

## What's deliberately out of scope for v1

- Embedding a *graphical* (VGA/framebuffer) display inside this app's own
  window - QEMU still opens its own gtk/sdl window for `Display = "gtk"`/
  `"sdl"`. `Display = "vnc"` (see "VNC" above) decouples the guest's screen
  from any window closing the VM, same as `console` already does for text,
  but connecting to actually *see* it still needs an external VNC client -
  no viewer is embedded in this app yet.
- Snapshots, device passthrough, custom network topologies - all reachable
  today through "Extra arguments" on the VM form, not yet through dedicated UI.
- For riscv-emu VMs specifically: `--trace`/`--max`/register-dump controls (the
  interpreter's own debugging flags) are deliberately not exposed here - this
  is the "run it like a normal program" frontend, not a debugger front end.
  Use riscv-emu.exe directly from a terminal for that, same as before.

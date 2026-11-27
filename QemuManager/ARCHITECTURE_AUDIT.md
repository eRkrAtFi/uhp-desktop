# Hypervisor Architecture Audit – Phase 1

> **Historical snapshot, 2026-11-13** - before this repo even had git, let
> alone the `IEngine`/`EnvironmentConfig` refactor, the `v*` release line,
> or the Host/Storage/Networking/Engines pages. Kept as-is (not rewritten)
> because it is itself a point-in-time read of the code, same as this
> project's own phase-by-phase README logs - editing it after the fact
> would misrepresent what it actually found on that date. Status against
> each finding below, confirmed against the code as of v0.6.0:
>
> - §1/§9/Critical Risk #2 (**no git**): fixed - this has been a real git
>   repo with PR-based review (see the root `CLAUDE.md`) since shortly
>   after this audit.
> - §4/Critical Risk #1 (**a VM that is still running shows as "Stopped"
>   after a restart**): fixed - `RuntimeStateStore`/`TryReattach`, see
>   `QemuManager/README.md`'s own "Surviving an app restart" section.
> - §4 (**host capabilities not modeled - no WHPX check**): fixed - the
>   Engines page's own `WhpxStatus` (`WHvGetCapability`, the same check
>   QEMU's `-accel whpx` itself makes).
> - §5/Critical Risk #3 (**no guided machine-type/firmware for non-x86**):
>   fixed - `Platform.MachineType`/`Firmware.FirmwarePath`, with
>   `ConfigField.DependsOn` suggesting/requiring a value per architecture;
>   see `QemuManager/README.md`'s "Guided machine type / firmware" section.
> - §6 (**storage: qcow2-only creation, format guessed from extension,
>   no multi-disk/pool concept**): partially addressed, not a pool - the
>   Storage page can create either qcow2 or raw and tracks every disk in
>   a small registry; format for riscv-emu's own validation is now read
>   from the file's real content, not its extension (`DiskImageService.
>   LooksLikeQcow2`). No storage-pool/multi-host concept exists, same as
>   this audit found.
> - §6 (**networking: binary NAT/none choice, no port forwarding/bridge**):
>   still true - the Networking page (new) is a read-only overview of the
>   same choice, nothing more.
> - §8 (**no QMP timeout**): fixed - see `QemuManager/README.md`'s "QMP
>   timeouts" section.
> - §9 (**`QemuManager.SelfTest` only covers the QEMU engine**): fixed -
>   it has covered both engines, plus everything from the pages this
>   audit predates, since.
> - §9 (**dead ternary, duplicated resource-sampling logic**): fixed -
>   see `QemuManager/README.md`'s "Closing the audit's remaining
>   low-priority items" section.
> - §3/§7 (**QMP has no auth; no multi-node/web-API layer exists**):
>   still true, and still not attempted - no network-facing surface
>   exists yet (see the root [`README.md`](../README.md)'s own Status
>   and [`vision/VISION.md`](../vision/VISION.md)'s roadmap for what is
>   and is not planned toward that).
>
> See the root [`README.md`](../README.md)'s own Status section for
> what is current right now - this document is not it.

Read-only audit. No files were modified, moved, renamed or deleted to produce
this document, and no dependencies were installed. Every finding below is
grounded in code actually read in this checkout on 2026-11-13, not assumed.

## 1. Existing architecture overview

`QemuManager` is a single-process Windows desktop app (.NET 8, WPF,
self-contained single-file publish). There is no separate backend, no
database, no network-facing API, and no multi-host concept today — this is a
**local desktop virtualization front end**, architecturally closer to
virt-manager/GNOME Boxes than to Proxmox/OpenStack, despite the project
context's stated multi-node/web ambitions. That gap between current reality
and stated ambition is the central theme of this audit.

Two independent, non-polymorphic "engines" exist side by side:

- **QEMU engine** — drives real `qemu-system-<arch>.exe`, controlled over
  QEMU's own QMP protocol (JSON-over-TCP, loopback).
- **RiscvEmu engine** — drives this repo's own hand-written RISC-V
  interpreter (`riscv-emu.exe`), controlled only via process stdio; no
  control protocol equivalent to QMP exists for it.

They share exactly one abstraction, `IConsoleSource` (3 members: an output
event, `SendInput`, `ForceStop`), used only by `ConsoleWindow`. Everywhere
else — `MainWindow.xaml.cs` in particular — the two engines are addressed as
two concrete, unrelated classes (`VmProcessManager`, `RiscvEmuProcessManager`)
selected by an `if (IsRiscvEmu(item))` branch repeated at every call site.

There is **no version control** for this codebase (`git status` from the
project root returns "not a git repository"). This is independent of the
application's own architecture but material to any of the audit tasks below
that assume safe, incremental, reviewable change.

## 2. Actual project module map

```
QemuManager/
├── QemuManager/                      WPF app (net8.0-windows, self-contained win-x64 publish)
│   ├── Models/
│   │   ├── VmConfig.cs               persisted VM definition (flat, Kind-discriminated)
│   │   └── VmListItem.cs             UI-facing wrapper: live status/CPU%/RAM over VmConfig
│   ├── Services/
│   │   ├── AppPaths.cs               %APPDATA%\QemuManager\{vms\, settings.json}
│   │   ├── AppSettings.cs            QemuDir / RiscvEmuDir, single JSON file
│   │   ├── VmStore.cs                one JSON file per VM under vms\ — whole persistence layer
│   │   ├── QemuLocator.cs            discovers qemu-system-*.exe / qemu-img.exe
│   │   ├── QmpClient.cs              QMP protocol client (newline-JSON over TCP)
│   │   ├── VmProcessManager.cs       QEMU engine: build args, start/stop, QMP status, console pump
│   │   ├── DiskImageService.cs       wraps qemu-img.exe create
│   │   ├── RiscvEmuLocator.cs        discovers riscv-emu.exe / boot/firmware.bin
│   │   ├── RiscvEmuBoot.cs           builds riscv-emu.exe's argv (C# port of boot.sh)
│   │   ├── DtbBuilder.cs             FDT/DTB builder (C# port of make_dtb.py)
│   │   ├── RiscvEmuProcessManager.cs RiscvEmu engine: start/stop, console pump
│   │   └── IConsoleSource.cs         3-member interface shared by both *ProcessManager classes
│   ├── MainWindow.xaml(.cs)          VM list + toolbar; owns one instance of EACH process manager
│   ├── VmEditWindow / RiscvEmuEditWindow / NewVmKindWindow / SettingsWindow / CreateDiskWindow
│   └── ConsoleWindow.xaml(.cs)       live text console, takes an IConsoleSource
└── QemuManager.SelfTest/             console harness; <Compile Include> links 7 files from
                                       QemuManager/{Services,Models} directly — no project
                                       reference, so it only ever covers the QEMU engine path
```

Languages/frameworks: C# / .NET 8 / WPF (+ WinForms, for `FolderBrowserDialog`
only) throughout. No other language, no ORM, no database engine, no web
framework, no IPC framework beyond raw `TcpClient` for QMP. `riscv-emu/` is a
separate, independently-built C project this app shells out to as a black
box (no shared code, only the generated command line and files on disk).

## 3. QEMU integration

**Process lifecycle** (`VmProcessManager.StartAsync`): resolves the binary via
`QemuLocator`, allocates a free TCP port for QMP by binding then immediately
releasing a `TcpListener` (`GetFreeTcpPort`), builds the full argv in
`BuildArguments`, starts the process, and polls for QMP readiness
(`TryConnectQmpWithRetryAsync`: up to 30 × 200 ms = 6 s).

**QMP connection management** (`QmpClient`): one TCP connection per logical
operation (`StartAsync`'s readiness probe, every `StopGracefullyAsync` /
`QueryGuestStatusAsync` call each open and close their *own* `QmpClient`)
rather than one persistent connection reused for the VM's lifetime. This is
simple and correct but means every status poll pays a fresh TCP handshake +
QMP greeting + capabilities-negotiation round trip (3 message exchanges)
every 2 seconds per running QEMU VM.

**QMP command handling**: `ExecuteAsync` writes one `{"execute": ...}` line
and loops `ReadLineAsync` until it sees a `"return"` or `"error"` key,
skipping interleaved `"event"` notifications. Correct per the QMP spec.

**Error handling and reconnection**: `StopGracefullyAsync` catches any
exception from the fresh connect+powerdown and reports "Use Force Stop
instead." There is **no timeout** anywhere on `ReadLineAsync` — if QEMU's QMP
socket accepted the connection but then never answers (hung QEMU, a bug in a
future device model, etc.), a status poll or stop request blocks forever.
Today this is low-impact (one `await` per button click / 2-second timer
tick, not a frozen UI, since all QMP calls are already `async`), but it is a
real gap for any future scenario where QMP calls must have a bounded
latency (e.g. a web API request).

**Process monitoring**: `Process.Exited` (via `EnableRaisingEvents`) plus a
2-second `DispatcherTimer` poll of `query-status` for state text, and
separately of `Process.TotalProcessorTime`/`WorkingSet64` for resource
usage. Two independent polling mechanisms with no shared cadence
abstraction, but both are cheap and currently correct.

**VM state synchronization**: `RunningVm.State` (`Stopped/Starting/Running/
Stopping`) is tracked only in `VmProcessManager`'s in-memory `_running`
dictionary — **not persisted anywhere**. Note also that `StartAsync` sets
`running.State = connected ? VmRunState.Running : VmRunState.Running;`
(`VmProcessManager.cs:112`) — the ternary is a no-op; the code already
acknowledges in its own comment that QMP connectivity is deliberately not
treated as a distinct state, but the dead ternary is worth cleaning up for
clarity's sake alone.

## 4. Virtual machine model

`VmConfig` is one flat class holding **both** engines' fields
simultaneously (`Architecture`, `Accelerator`, `DiskPath`, `CdromPath`,
`NetworkMode`, `Display`, `BootOrder` for Qemu; `KernelPath`, `InitrdPath`
for RiscvEmu), discriminated at read time by a plain `string Kind`. This is
a reasonable, honestly-documented tradeoff for exactly two engines (the
doc-comments on each field say so explicitly) and is **not** recommended for
immediate refactoring — introducing a capability/plugin abstraction now,
before a third concrete engine exists to generalize against, would be
speculative generalization against a sample size of two.

**Definitions vs. runtime state are not separated.** `VmConfig` (desired/
persisted state) is clean of any runtime fields — good. But the *actual*
runtime state (`RunningVm`/`RunningRiscvEmu`, including the live
`Process` handle and QMP port) lives only in each process manager's
in-memory dictionary, keyed by `VmConfig.Id`, and is never written to disk.
Consequence: if `QemuManager.exe` itself is killed or crashes while a VM is
running, the real `qemu-system-*.exe`/`riscv-emu.exe` process is **not**
terminated (no Job Object ties child lifetime to the parent — confirmed by
reading `ProcessStartInfo` construction in both managers, which sets no such
association), but on the next app launch that VM will show as **"Stopped"**
— actively incorrect, not just "status unknown" — because nothing persists
the PID or QMP port to re-discover it. This is the single biggest
correctness gap found in this audit (see Critical Risks, #1).

**Host capabilities** are not modeled at all: nothing today asks "does this
host actually support `whpx`?" before offering it as an accelerator choice,
or "which architectures does the configured QEMU install actually ship?"
beyond listing which `qemu-system-*.exe` files are present.

## 5. Architecture (CPU) support

Architecture selection (`VmConfig.Architecture`) only picks *which binary* to
run (`qemu-system-<arch>.exe`/`-w.exe`) via `QemuLocator`. Beyond that,
**`BuildArguments` is entirely architecture-agnostic**: it never emits `-M`
(machine type), `-cpu` (CPU model), or `-bios`/firmware selection for any
architecture. For x86-64 this is harmless (QEMU's x86 default machine type
and firmware are usually sufficient). For architectures that normally
*require* an explicit machine type to boot anything meaningful — ARM64's
`qemu-system-aarch64` has no usable default machine type and needs `-M
virt` plus a firmware/EFI image at minimum — this means **ARM64 support is
only reachable today through "Extra arguments,"** with no guided UI despite
architecture selection existing in the VM form. This is a real gap between
advertised multi-architecture support and what a user can actually
accomplish without already knowing QEMU's own CLI by heart.

RISC-V is handled completely differently depending on `Kind`: QEMU's own
`qemu-system-riscv64` (if present in the configured QEMU dir) follows the
same generic, under-specified path as any other architecture above; the
*separate* `RiscvEmu` kind is this repo's own fixed-configuration
interpreter (single hart, 256 MiB RAM, `RamBase = 0x80000000`, no
acceleration modes, no disk/network at all) — these are two unrelated code
paths that happen to target the same ISA, not a shared RISC-V abstraction.

## 6. Storage and networking

**Storage**: one optional disk (`DiskPath`) + one optional CD-ROM
(`CdromPath`) per VM, format guessed from file extension
(`VmProcessManager.GuessFormat`), disk interface hardcoded to `if=virtio`.
Disk *creation* only supports `qcow2` (`DiskImageService.CreateQcow2Async`
shells out to `qemu-img create -f qcow2`). No snapshot, multi-disk, storage
pool, or backend-abstraction concept exists; none of this blocks the
current use case (a user manually picks an existing image or creates one
qcow2 at a time) but all of it would need to exist before any of the
brainstormed storage-pool/Ceph/multi-host ideas apply.

**Networking**: a binary choice — `"user"` (QEMU's built-in NAT, with the
NIC model hardcoded to `e1000` — `VmProcessManager.cs:365`) or `"none"`. No
bridge, VLAN, custom MAC, or port-forwarding UI; reachable only through
"Extra arguments," same caveat as architecture-specific flags above.

## 7. Multi-node readiness

There is currently **no structural blocker** to eventually extracting the
process-management logic behind a network-facing API (the `VmConfig` /
`VmStore` / `*ProcessManager` separation is already reasonably clean for
that), but there is also **nothing built toward it today**: all state is
in-process memory on one machine, `VmStore` reads/writes a local directory
directly, and nothing in the codebase has any concept of "which host is
this VM on." Multi-node is a **net-new layer**, not an extension of
anything that exists — consistent with what the brainstormed roadmap
already assumed, now confirmed against the real code rather than guessed.

## 8. Security

- **QMP has no authentication**, listening on `127.0.0.1` only
  (`-qmp tcp:127.0.0.1:<port>,server,nowait`). Acceptable for a
  single-user local desktop app where the only other process that could
  connect to that port is something already running as the same Windows
  user; **not** acceptable as-is if QMP is ever exposed beyond loopback
  (e.g. to a remote Host Agent) — would need TLS/auth or a different IPC
  mechanism (local Unix socket / named pipe) before that happens.
- **Port allocation has a narrow TOCTOU window**: `GetFreeTcpPort` binds a
  `TcpListener` to port 0, reads the OS-assigned port, then immediately
  stops the listener and hands the port number to QEMU's `-qmp` argument.
  Between "stop the listener" and "QEMU binds the same port," another
  process could theoretically take it. Low likelihood, low impact (QEMU
  would just fail to start QMP, which `TryConnectQmpWithRetryAsync` already
  surfaces as a non-fatal "did not come up in time" message), not worth
  fixing proactively.
- **Command-line construction does not concatenate a shell string** —
  every argument goes through `ProcessStartInfo.ArgumentList`, which avoids
  the classic shell-injection class of bug entirely. The one hand-rolled
  parser, `VmProcessManager.SplitArguments` (splits the free-form "Extra
  arguments" field on whitespace, honoring double quotes), doesn't escape
  embedded quote characters, but its input is the local user's own
  deliberately-typed VM configuration, not external/untrusted input — a
  parsing edge case here is a usability bug, not a security boundary.
- **No authentication/authorization layer exists anywhere** — correct and
  sufficient for today's scope (single local user, no network-facing
  surface at all), but worth stating plainly: nothing here is reusable for
  the "planned web-based management interface" from the project context;
  that will need an entirely new identity/RBAC layer built from scratch.
- VM configs and settings are plain JSON under `%APPDATA%\QemuManager\`,
  normal user-scoped file permissions — fine for current scope, nothing to
  flag.

## 9. Technical debt

- **No version control for the repository at all.** Classified CRITICAL
  below — not because of anything in the application's design, but because
  every other recommendation in this document (and in the broader
  brainstormed roadmap) assumes small, reviewable, revertible changes,
  which is not meaningfully possible without `git` history today.
- **UI-to-engine coupling**: `MainWindow.xaml.cs` constructs both
  `VmProcessManager` and `RiscvEmuProcessManager` directly and repeats an
  `if (IsRiscvEmu(item)) ... else ...` branch at six call sites
  (`StartButton_Click`, `StopButton_Click`, `ForceStopButton_Click`,
  `ConsoleButton_Click`, `RefreshRunStates`, `RefreshGuestStatusesAsync`).
  This is real, observable duplication today (not a hypothetical future
  problem), though it is a manageable amount at exactly two engines.
- **Duplicated resource-usage sampling logic**: `GetResourceUsage` in
  `VmProcessManager` and `RiscvEmuProcessManager` are near-identical
  (`TotalProcessorTime` delta ÷ wall-clock delta ÷ `Environment.ProcessorCount`).
  Minor, low-priority duplication.
- **No QMP call timeout** (see §3) — fine today, a real gap before any
  network-facing or SLA-bound use.
- **No persisted runtime state / no orphan-process recovery** (see §4) —
  the most concrete, user-visible bug-shaped gap found in this audit.
- **`QemuManager.SelfTest` only exercises the QEMU engine path.** Its
  `<Compile Include>` list pulls in `VmProcessManager.cs`, `QmpClient.cs`,
  `QemuLocator.cs`, `DiskImageService.cs` — nothing from the RiscvEmu side
  (`RiscvEmuProcessManager.cs`, `RiscvEmuBoot.cs`, `DtbBuilder.cs`,
  `RiscvEmuLocator.cs` are entirely untested by any harness in the repo
  today, other than the GUI itself).
- **Dead ternary** in `VmProcessManager.StartAsync` (`connected ?
  VmRunState.Running : VmRunState.Running`, line 112) — cosmetic, call out
  for cleanup only.

## Critical technical risks

1. **A VM that is actually still running shows as "Stopped" after
   `QemuManager.exe` restarts** (crash, update, manual kill) — no PID/QMP
   port is persisted, no Job Object ties the child process's lifetime to
   the parent's, so the real QEMU/riscv-emu process silently keeps running
   detached from the app's knowledge of it. A user could end up with
   multiple un-tracked copies of the same VM's disk opened concurrently by
   starting it "again." **(CRITICAL)**
2. **No version control for the codebase.** Any further architectural work
   — the audit's own "small, independently testable changes" requirement —
   is unsafe without it. **(CRITICAL, but orthogonal to the app's own code)**
3. **Advertised multi-architecture support has no guided path for anything
   beyond x86-64** (no machine-type/firmware handling for ARM64 in
   particular) — not a crash risk, but a real gap between what the project
   claims to support and what a user can do without manually writing QEMU
   flags themselves. **(HIGH)**
4. **No QMP operation has a timeout.** Not observed to cause a problem in
   this app's current usage pattern, but is a latent risk the moment any
   QMP call needs a bounded response time (e.g. behind a future API).
   **(MEDIUM)**

## Recommended incremental improvements

Each item lists exactly the files it would touch; none of these have been
implemented as part of this audit.

| # | Recommendation | Affected files | Priority |
|---|---|---|---|
| 1 | Initialize git, commit current state as a baseline before any further architectural work | (repo root) | **CRITICAL** |
| 2 | Persist enough runtime identity (PID + QMP port, or PID + a "riscv-emu" marker) per running VM so the app can detect "still running after restart" instead of silently reporting Stopped | `VmStore.cs`, `VmProcessManager.cs`, `RiscvEmuProcessManager.cs`, `VmConfig.cs` (or a new sidecar runtime-state file, to avoid polluting the persisted VM definition) | **CRITICAL** |
| 3 | Add a UI-exposed machine-type/firmware field (or at least a documented, guided "Extra arguments" preset) for non-x86 architectures, starting with ARM64's `-M virt` | `VmEditWindow.xaml(.cs)`, `VmConfig.cs`, `VmProcessManager.BuildArguments` | **HIGH** |
| 4 | Add a bounded timeout to `QmpClient.ExecuteAsync`'s read loop | `QmpClient.cs` | **MEDIUM** |
| 5 | Extend `QemuManager.SelfTest` to also cover the RiscvEmu engine path (currently zero automated coverage for it) | `QemuManager.SelfTest.csproj`, `QemuManager.SelfTest/Program.cs` | **MEDIUM** |
| 6 | Remove the dead `connected ? Running : Running` ternary and either surface QMP-not-ready as a distinct, visible state or drop the `connected` variable | `VmProcessManager.cs` | **LOW** |
| 7 | Factor the duplicated `GetResourceUsage` CPU%/memory sampling logic into one shared helper taking a `Process` | `VmProcessManager.cs`, `RiscvEmuProcessManager.cs` | **LOW** |

**Dependencies between the above**: #2 is a prerequisite for any future
multi-node/Host Agent work (you cannot hand off "what's running where" to a
remote agent if the local app itself cannot answer that after its own
restart). #1 is a prerequisite for safely attempting any of the others.
#3 and #5 are independent of each other and of #2/#4.

**Explicitly not recommended right now**: introducing an `IVirtualizationEngine`
plugin abstraction, a database, a REST/WebSocket API, or a Host Agent. All
four appeared in the earlier brainstorm as the long-term target architecture,
but nothing in this audit found a concrete problem today that any of them
would fix — with exactly two engines and one user on one machine, that
generalization would be speculative. Revisit once a third engine or a second
host is an actual, not hypothetical, requirement.

## Prioritized implementation roadmap

1. **Now**: #1 (git init + baseline commit) — unblocks everything else.
2. **Next**: #2 (runtime-state persistence / orphan detection) — the one
   change that fixes a real, user-visible correctness bug today.
3. **Then**: #3 (guided non-x86 machine-type support) — closes the gap
   between advertised and actual multi-architecture support.
4. **Opportunistic / low-risk cleanup**: #4, #5, #6, #7 — none are
   blocking, all are safe to pick up independently whenever convenient.
5. **Not scheduled**: the plugin/Core/API/Host-Agent architecture from the
   earlier brainstorm — revisit only once a concrete third engine or
   second host exists to design against.

---

*No source files were modified, moved, renamed or deleted in the course of
this audit. Stop here — implementation of any recommendation above should
be a separate, explicitly-requested step.*

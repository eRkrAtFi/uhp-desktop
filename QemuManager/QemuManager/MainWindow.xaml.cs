using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using QemuManager.Models;
using QemuManager.Services;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Point = System.Windows.Point;

namespace QemuManager;

public partial class MainWindow : Window
{
    // The one and only object this window talks to for anything VM-related
    // - no VmStore, no per-engine manager, and no
    // "if (Config.Kind == "RiscvEmu")" branching lives here any more; all
    // three now live once, inside VmService (see UHP.Core).
    private readonly IVmService _vmService = new VmService();
    private AppSettings _settings = new();
    private readonly ObservableCollection<VmListItem> _items = new();
    private readonly Dictionary<string, ConsoleWindow> _consoleWindows = new();

    // A separate view over _items for the grid, so the search box's filter
    // only hides rows from the grid - not from VirtualMachinesNode's own
    // ItemsSource further up, which is bound directly to _items. Both would
    // otherwise share WPF's one cached "default view" per source collection,
    // and filtering one would silently filter the other too.
    private readonly CollectionViewSource _vmGridSource = new();
    private double _recentTasksExpandedHeight = 104;

    public MainWindow()
    {
        InitializeComponent();
        _vmGridSource.Source = _items;
        _vmGridSource.Filter += VmGridFilter;
        VmList.ItemsSource = _vmGridSource.View;
        VirtualMachinesNode.ItemsSource = _items;
        HostNameText.Text = Environment.MachineName;
        // Set here, not via IsSelected="True" in XAML: a TreeViewItem fires
        // SelectedItemChanged synchronously as soon as its IsSelected setter
        // runs, which for an XAML-declared value happens mid-InitializeComponent
        // - before VirtualMachinesPage/HostPage/etc. further down in the same
        // document have been constructed yet, so the handler's very first
        // access of one of them threw a NullReferenceException and crashed
        // the app before its window ever appeared. Both fields exist by now.
        VirtualMachinesNode.IsSelected = true;

        // Ask, rather than silently copying (including every disk image,
        // which the old behavior did) - see AppPaths.HasUnimportedLegacyData's
        // own remarks for why. Must run before LoadVms() below: if the
        // user says Import, the copied vms/*.json need to already be in
        // place for that first LoadVms() to pick them up, not a second,
        // separate refresh afterward.
        if (AppPaths.HasUnimportedLegacyData())
        {
            var result = MessageBox.Show(this,
                "Found existing UHP data in %APPDATA%. Import it?\n\n" +
                "Only Environment configurations are imported - disk images are not copied and stay where they already are.",
                "Import existing data?", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                var imported = AppPaths.ImportLegacyConfigs(AppContext.BaseDirectory,
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                _vmService.RecordTask("Import", "%APPDATA%", true, $"Imported {imported} Environment configuration(s).");
            }
        }

        _settings = AppSettings.Load();
        // A VM's process exiting without this window's own Power On/Shut
        // Down/Force Stop handlers having asked for it (closing a QEMU
        // gtk/sdl window directly, a crash, or a graceful ACPI shutdown
        // finally completing well after Shut Down's own task already
        // completed) used to be detected and logged right here - moved
        // into VmService itself (see its own DetectUnexpectedStop), since
        // Recent Tasks is now "história v jadre", Core's own history, not
        // something the WPF layer computes. This handler's only job now is
        // refreshing the grid's own Running/Stopped state.
        _vmService.StateChanged += () => Dispatcher.Invoke(RefreshRunStates);
        _vmService.TasksChanged += () => Dispatcher.Invoke(RefreshRecentTasks);
        // Replaces the DispatcherTimer this window used to own and poll
        // GetSnapshot/QueryGuestStatusAsync from every 2 seconds - that
        // loop now lives in VmService itself (a plain, non-WPF Timer), and
        // this window just reacts to what it pushes. The Dispatcher.Invoke
        // here is still correct and still necessary: VmService's timer
        // fires on a thread-pool thread, and every VmListItem property
        // write below must happen on the UI thread for WPF's bindings to
        // see it safely.
        _vmService.StatsReceived += (vmId, stats) => Dispatcher.Invoke(() => ApplyStats(vmId, stats));

        LoadVms();
        RefreshToolbarState();
        RefreshRecentTasks();
        QemuDirBox.Text = _settings.QemuDir;
        RiscvEmuDirBox.Text = _settings.RiscvEmuDir;

        // Both fire-and-forget, not awaited, for the same reason
        // ConsolePreviewTerminal.InitializeAsync below already is: must
        // not block this constructor, and nothing downstream depends on
        // either having completed yet - the Engines page's own initial
        // "not found"/"-" text is what EnginesPage.xaml declares in XAML
        // for every TextBlock these calls go on to fill in once they do.
        _ = RefreshEnginesPageAsync();

        // Fire-and-forget, not awaited: WebView2's own async setup must
        // not block this constructor (ShowDialog/Show need to return
        // promptly) - Attach itself is always safe to call even before
        // this finishes (see TerminalControl's own remarks), so nothing
        // downstream actually depends on this having completed yet.
        _ = ConsolePreviewTerminal.InitializeAsync();
    }

    private void LoadVms()
    {
        _items.Clear();
        // VmService.LoadAll already re-adopts any VM a previous instance of
        // this app (or this one, after a crash) left running - see
        // ARCHITECTURE_AUDIT.md's Critical Risk #1 and
        // VmProcessManager/RiscvEmuProcessManager.TryReattach.
        foreach (var vm in _vmService.LoadAll())
            _items.Add(new VmListItem(vm, _vmService));
        RefreshRunStates();
    }

    private static readonly SolidColorBrush EngineFoundBrush = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush EngineErrorBrush = new(Color.FromRgb(0xE0, 0xA5, 0x30));
    private static readonly SolidColorBrush EngineNotFoundBrush = new(Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>Re-queries both engines' own install status (IVmService.
    /// GetEngineStatusesAsync - path found/not found, --version,
    /// architectures, how many Environments use each) and fills in the
    /// Engines page. Called on startup, whenever the Engines node is
    /// selected, and after QemuDirBox/RiscvEmuDirBox actually change -
    /// not cached, since this page's whole point is "what is actually
    /// there right now," the same reason IVmService's own doc comment on
    /// GetEngineStatusesAsync gives for never caching it.</summary>
    private async Task RefreshEnginesPageAsync()
    {
        var statuses = await _vmService.GetEngineStatusesAsync(_settings);
        foreach (var status in statuses)
        {
            var (dot, statusText, versionText, archText, countText) = status.EngineId == "Qemu"
                ? (QemuStatusDot, QemuStatusText, QemuVersionText, QemuArchitecturesText, QemuEnvironmentCountText)
                : (RiscvEmuStatusDot, RiscvEmuStatusText, RiscvEmuVersionText, RiscvEmuArchitecturesText, RiscvEmuEnvironmentCountText);

            dot.Fill = status.Status switch
            {
                EngineStatus.Found => EngineFoundBrush,
                EngineStatus.Error => EngineErrorBrush,
                _ => EngineNotFoundBrush,
            };
            statusText.Text = status.Status switch
            {
                EngineStatus.Found => "Found",
                EngineStatus.Error => "Found, but --version failed",
                _ => "Not found",
            };
            versionText.Text = status.VersionText ?? "-";
            archText.Text = status.SupportedArchitectures.Count > 0 ? string.Join(", ", status.SupportedArchitectures) : "-";
            countText.Text = status.EnvironmentCount.ToString();
        }

        // Only QEMU can actually use WHPX (Platform.Accelerator == "whpx")
        // - riscv-emu has no accelerator choice of its own (always its
        // own interpreter) - so this note lives on QEMU's own card, not
        // duplicated on riscv-emu's or hoisted to the page as a whole.
        QemuWhpxText.Text = WhpxStatus.IsEnabled()
            ? "Enabled - QEMU Environments with Accelerator set to WHPX can use it."
            : "Not available - QEMU Environments with Accelerator set to WHPX will fail to start; use TCG instead.";
    }

    private async void QemuBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the folder containing qemu-system-*.exe",
            SelectedPath = QemuDirBox.Text,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        QemuDirBox.Text = dialog.SelectedPath;
        await SaveQemuDirAsync();
    }

    private async void QemuDirBox_LostFocus(object sender, RoutedEventArgs e) => await SaveQemuDirAsync();

    private async Task SaveQemuDirAsync()
    {
        var path = QemuDirBox.Text.Trim();
        if (path == _settings.QemuDir) return; // unchanged - avoid a redundant re-query/save on every unrelated LostFocus
        _settings.QemuDir = path;
        _settings.Save();
        await RefreshEnginesPageAsync();
    }

    private async void RiscvEmuBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the riscv-emu folder (contains riscv-emu.exe and boot\\firmware.bin)",
            SelectedPath = RiscvEmuDirBox.Text,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        RiscvEmuDirBox.Text = dialog.SelectedPath;
        await SaveRiscvEmuDirAsync();
    }

    private async void RiscvEmuDirBox_LostFocus(object sender, RoutedEventArgs e) => await SaveRiscvEmuDirAsync();

    private async Task SaveRiscvEmuDirAsync()
    {
        var path = RiscvEmuDirBox.Text.Trim();
        if (path == _settings.RiscvEmuDir) return;
        _settings.RiscvEmuDir = path;
        _settings.Save();
        await RefreshEnginesPageAsync();
    }

    /// <summary>Host page's own facts (IVmService.GetHostInfo) - cheap
    /// and synchronous (no process spawned, unlike the Engines page), so
    /// this also doubles as part of RefreshSummary's own per-stats-tick
    /// refresh, keeping the running-count/CPU/memory totals live while
    /// the page happens to be open, not just on first navigating to it.</summary>
    private void RefreshHostPage()
    {
        var host = _vmService.GetHostInfo();
        HostOsText.Text = host.OperatingSystem;
        HostCpuText.Text = host.CpuDescription;
        HostMemoryText.Text = host.MemoryDescription;
        HostFreeDiskText.Text = host.FreeDiskSpaceDescription;
        HostWhpxText.Text = host.WhpxEnabled ? "Enabled" : "Not available";
        HostRunningCountText.Text = $"{host.RunningEnvironments} / {host.TotalEnvironments}";
        HostTotalCpuText.Text = $"{host.TotalCpuPercent:0.#}%";
        HostTotalMemoryText.Text = $"{host.TotalMemoryBytes / (1024.0 * 1024):0} MB";
    }

    /// <summary>Storage page's own list (IVmService.GetDiskInventoryAsync)
    /// - re-read every time the Storage node is selected or an action on
    /// this page changes something, not kept continuously live the way
    /// Host/the Environment grid are (nothing here changes on its own
    /// between user actions the way a running VM's CPU% does).</summary>
    private async Task RefreshStoragePageAsync()
    {
        var disks = await _vmService.GetDiskInventoryAsync(_settings);
        DiskList.ItemsSource = disks.Select(d => new DiskListItem(d)).ToList();
        RefreshStorageToolbarState();
    }

    private DiskListItem? SelectedDisk => DiskList.SelectedItem as DiskListItem;

    private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshStorageToolbarState();

    private void RefreshStorageToolbarState()
    {
        var disk = SelectedDisk;
        OpenDiskFolderButton.IsEnabled = disk != null;
        RemoveDiskButton.IsEnabled = disk != null && disk.CanRemove;
    }

    private async void CreateDiskButton_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DisksDir);
        var suggestedPath = Path.Combine(AppPaths.DisksDir, "disk.qcow2");
        var dialog = new CreateDiskWindow(_settings.QemuDir, suggestedPath, _vmService) { Owner = this };
        if (dialog.ShowDialog() == true) await RefreshStoragePageAsync();
    }

    private void OpenDiskFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk;
        if (disk == null) return;
        // explorer.exe /select highlights the file itself rather than
        // just opening its containing folder - a plain OS-shell action,
        // the same kind of UI-layer-only concern ConsoleWindow's own
        // window already is, nothing for UHP.Core to own.
        try { Process.Start("explorer.exe", $"/select,\"{disk.Path}\""); }
        catch (Exception ex) { MessageBox.Show(this, $"Could not open folder: {ex.Message}", "Open folder", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void RemoveDiskButton_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk;
        if (disk == null || !disk.CanRemove) return;
        var result = MessageBox.Show(this,
            $"Permanently delete {disk.Path}?\n\nThis disk is not used by any Environment.",
            "Remove disk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        await _vmService.DeleteDiskFileAsync(disk.Path);
        await RefreshStoragePageAsync();
    }

    /// <summary>Networking page's own list (IVmService.GetNetworkInventory)
    /// - read-only, no toolbar, so unlike Storage this never needs its
    /// own explicit re-read beyond selecting the node (Platform.NetworkMode
    /// cannot change while this page is open - that only happens in the
    /// editor, which always returns to the grid, not this page, on Save).</summary>
    private void RefreshNetworkingPage()
    {
        NetworkList.ItemsSource = _vmService.GetNetworkInventory().Select(n => new NetworkListItem(n)).ToList();
    }

    private VmListItem? SelectedItem => VmList.SelectedItem as VmListItem;

    private void VmGridFilter(object sender, FilterEventArgs e)
    {
        var text = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) { e.Accepted = true; return; }
        var vi = (VmListItem)e.Item;
        e.Accepted = vi.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                  || vi.Kind.Contains(text, StringComparison.OrdinalIgnoreCase)
                  || vi.Architecture.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => _vmGridSource.View.Refresh();

    private void VmList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshToolbarState();

    /// <summary>Opens the VM's own detail page - previously opened Edit
    /// (and only while stopped), which conflated "I want to look at this
    /// VM" with "I want to change its configuration". Edit is still one
    /// click away (its own button, in the grid toolbar and on the detail
    /// page itself) for whoever actually wants that.</summary>
    private void VmList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (VirtualMachinesNode.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem container)
            container.IsSelected = true;
    }

    // Set by VmList_PreviewMouseRightButtonDown (which runs first) and read
    // by VmList_ContextMenuOpening (which runs right after, before the
    // menu is actually shown) - WPF's DataGrid does not select a row on
    // right-click the way a left-click does, so without this a right-click
    // on a row other than the currently-selected one would open a menu
    // whose Power On/Shut Down/.../Delete all still act on whatever was
    // selected before, not the row the user just right-clicked.
    private bool _rightClickHitRow;

    private void VmList_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        _rightClickHitRow = row != null;
        if (row?.Item is VmListItem item) VmList.SelectedItem = item;
    }

    private void VmList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-clicking empty space below the last row only offers
        // Create VM - Power On/Shut Down/Force Stop/Console/Edit/Delete
        // all need a specific VM, which nothing under the cursor there is.
        var showVmActions = _rightClickHitRow;
        CtxSeparator1.Visibility = CtxStartItem.Visibility = CtxStopItem.Visibility = CtxForceStopItem.Visibility =
            CtxConsoleItem.Visibility = CtxSeparator2.Visibility = CtxEditItem.Visibility = CtxDeleteItem.Visibility =
            showVmActions ? Visibility.Visible : Visibility.Collapsed;
        RefreshToolbarState();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null && node is not T) node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        return node as T;
    }

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var kindDialog = new NewEnvironmentWindow { Owner = this };
        if (kindDialog.ShowDialog() != true) return;

        var vm = _vmService.CreateDraft(kindDialog.SelectedEngineId);
        if (kindDialog.IsBareMetal)
        {
            // The "Bare-metal / Embedded" template's own defaults - every
            // other field stays whatever CreateDraft already gives
            // riscv-emu (e.g. Platform.MemoryMB = 512). Kind is what
            // actually switches EnvironmentEditWindow over to the bare-
            // metal subset of fields (see GetConfigSchema's own
            // VisibleWhen/RequiredWhen); network defaults off - a bare
            // ELF this project ships an example of has no driver stack
            // to ever use virtio-net anyway, and leaving it on would
            // just be one more thing to explain was never doing anything.
            vm.Workload.Kind = WorkloadKind.BareMetalElf;
            vm.Platform.NetworkMode = "none";
            vm.Debug.GdbEnabled = true;
            vm.Debug.EnterSends = "LF";
        }
        bool? result = new EnvironmentEditWindow(vm, _settings, _vmService) { Owner = this }.ShowDialog();

        if (result == true)
        {
            _vmService.Save(vm);
            _items.Add(new VmListItem(vm, _vmService));
            RefreshSummary();
            _vmService.RecordTask("Create Environment", vm.Name, true, $"Created Environment '{vm.Name}'.");
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (_vmService.IsRunning(item.Config))
        {
            _vmService.RecordTask("Edit Environment", item.Config.Name, false, "Stop the Environment before editing its configuration.");
            return;
        }

        bool? result = new EnvironmentEditWindow(item.Config, _settings, _vmService) { Owner = this }.ShowDialog();

        if (result == true)
        {
            _vmService.Save(item.Config);
            item.RefreshFromConfig();
            _vmService.RecordTask("Edit Environment", item.Config.Name, true, $"Saved Environment '{item.Config.Name}'.");
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (_vmService.IsRunning(item.Config))
        {
            _vmService.RecordTask("Delete Environment", item.Config.Name, false, "Stop the Environment before deleting it.");
            return;
        }

        var result = MessageBox.Show(this, $"Delete '{item.Config.Name}'? This removes its configuration, not any disk image.",
            "Delete Environment", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _vmService.Delete(item.Config);
        _items.Remove(item);
        RefreshSummary();
        _vmService.RecordTask("Delete Environment", item.Config.Name, true, $"Deleted Environment '{item.Config.Name}'.");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        // StartAsync claims VmService's own per-VM "action in progress"
        // guard synchronously, before its first await - by the time this
        // line returns a Task to await, IsActionInProgress is already
        // true, so refreshing the toolbar right here (not only after the
        // await completes) is what actually disables the button for the
        // duration of the call, closing the double-click race a disabled-
        // only-after-the-fact button would not. StartAsync already records
        // its own "Power On" task (Running the instant it begins, via
        // TasksChanged - no separate "Starting..." line needed here any
        // more) and completes it with the real result below.
        var startTask = _vmService.StartAsync(item.Config, _settings);
        RefreshToolbarState();
        await startTask;

        // Only auto-opens when the engine says this VM's console is a live
        // stream this app can render itself - never for QEMU's own gtk/sdl
        // window or a VNC-only VM, same as before this refactor.
        if (_vmService.IsRunning(item.Config) && _vmService.GetConsoleAvailability(item.Config).kind == ConsoleAvailability.Stream)
            OpenConsoleWindow(item.Config);

        RefreshRunStates();
    }

    private async void StopButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        var shutdownTask = _vmService.ShutdownAsync(item.Config, _settings);
        RefreshToolbarState(); // see StartButton_Click's own remarks
        var result = await shutdownTask;
        RefreshRunStates();

        // Point 3: a timed-out graceful shutdown offers Force Stop - it
        // never happens on its own. Only riscv-emu's own leg can set
        // TimedOut (see ShutdownAsync's own remarks), and only while the
        // VM the user is looking at is still the same one this result is
        // actually about - SelectedItem could have changed during the
        // (up to RiscvShutdownTimeoutSeconds-long) wait.
        if (result.TimedOut && SelectedItem == item)
        {
            var offerForceStop = MessageBox.Show(this,
                $"'{item.Config.Name}' did not shut down within the configured timeout.\n\nForce-terminate it now? This is equivalent to pulling the power cord.",
                "Shut Down timed out", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (offerForceStop == MessageBoxResult.Yes)
            {
                _vmService.ForceStop(item.Config);
                RefreshRunStates();
            }
        }
    }

    private void ForceStopButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        var result = MessageBox.Show(this, $"Force-terminate '{item.Config.Name}'? This is equivalent to pulling the power cord.",
            "Force Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _vmService.ForceStop(item.Config);
        RefreshRunStates();
    }

    private void ConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        var (kind, detail) = _vmService.GetConsoleAvailability(item.Config);
        switch (kind)
        {
            case ConsoleAvailability.NotRunning:
                return;
            case ConsoleAvailability.Stream:
                OpenConsoleWindow(item.Config);
                break;
            case ConsoleAvailability.Vnc:
                _vmService.RecordTask("Console", item.Config.Name, detail != null,
                    detail != null ? $"VNC at {detail} - connect with any VNC client." : "VNC endpoint not available.");
                break;
            case ConsoleAvailability.ExternalWindow:
                _vmService.RecordTask("Console", item.Config.Name, false,
                    "Display is gtk/sdl - it shows its own window, not a console here. Edit the Environment and set Display to 'console' to change that.");
                break;
        }
    }

    private void GdbCopyButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item?.GdbAttachCommand is not { } command) return;
        System.Windows.Clipboard.SetText(command);
    }

    /// <summary>Idempotent: if a console for this VM is already open,
    /// activates it instead of opening a second one. _consoleWindows alone
    /// (a plain WPF dictionary, check-then-assign on the UI thread) should
    /// already guarantee that by itself - TryBeginConsoleSession is a real
    /// second, Core-level guard underneath it, not a cosmetic one; see
    /// VmService's own remarks for why it exists regardless.</summary>
    private void OpenConsoleWindow(EnvironmentConfig vm)
    {
        if (_consoleWindows.TryGetValue(vm.Id, out var existing))
        {
            existing.Activate();
            return;
        }
        if (!_vmService.TryBeginConsoleSession(vm.Id)) return; // a session is already claimed for this vmId elsewhere - do nothing rather than open a second window

        var window = new ConsoleWindow(vm, _vmService) { Owner = this };
        _consoleWindows[vm.Id] = window;
        window.Closed += (_, _) =>
        {
            _consoleWindows.Remove(vm.Id);
            _vmService.EndConsoleSession(vm.Id);
        };
        window.Show();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.RiscvShutdownTimeoutSeconds) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.RiscvShutdownTimeoutSeconds = dialog.RiscvShutdownTimeoutSeconds;
            _settings.Save();
        }
    }

    private void RefreshRunStates()
    {
        foreach (var item in _items)
        {
            item.State = _vmService.GetState(item.Config);
            if (!item.IsRunning) item.ClearResourceUsage();
        }
        RefreshSummary();
        RefreshToolbarState();
    }

    private void RefreshSummary()
    {
        // The navigator card this used to live in is gone (Phase 1 fix) -
        // the VM count moves to a small badge on the node itself, and the
        // running/total summary moves to the Host page's own "Running /
        // Total" tile (RefreshHostPage) - no longer duplicated here as a
        // second, separate line above it (PR #7 feedback).
        VmCountBadge.Text = _items.Count.ToString();
        RefreshHostPage();
    }

    /// <summary>Toolbar buttons (and their context-menu mirrors) enable or
    /// disable from the current selection, its run state, and whether an
    /// action is already in flight for it - instead of a click producing a
    /// "Select a VM first" message, or a double-click running Power On/
    /// Shut Down/Force Stop/Delete twice concurrently. Call after any
    /// change to any of those three (selection, a VM's Running/Stopped
    /// state, or VmService.IsActionInProgress). Console is deliberately
    /// not gated on "busy" - opening/activating it is instantaneous and has
    /// its own, separate idempotency guard (see OpenConsoleWindow).</summary>
    private void RefreshToolbarState()
    {
        var item = SelectedItem;
        var running = item != null && _vmService.IsRunning(item.Config);
        var busy = item != null && _vmService.IsActionInProgress(item.Config.Id);
        var selected = item != null;

        // Workload.SupportsGracefulShutdown is false for a bare-metal ELF
        // on either engine - there is no OS/shell to type
        // Workload.ShutdownCommand into (riscv-emu) and no ACPI handler
        // to react to a QMP power button event either (QEMU), so "Shut
        // Down" would just silently do nothing; Force Stop is the only
        // real way to stop one.
        var canShutDownGracefully = item != null && item.Config.Workload.SupportsGracefulShutdown;
        StartButton.IsEnabled = CtxStartItem.IsEnabled = DetailStartButton.IsEnabled = selected && !running && !busy;
        StopButton.IsEnabled = CtxStopItem.IsEnabled = DetailStopButton.IsEnabled = selected && running && !busy && canShutDownGracefully;
        ForceStopButton.IsEnabled = CtxForceStopItem.IsEnabled = DetailForceStopButton.IsEnabled = selected && running && !busy;
        ConsoleButton.IsEnabled = CtxConsoleItem.IsEnabled = DetailConsoleButton.IsEnabled = selected && running;
        EditButton.IsEnabled = CtxEditItem.IsEnabled = DetailEditButton.IsEnabled = selected && !running && !busy;
        DeleteButton.IsEnabled = CtxDeleteItem.IsEnabled = DetailDeleteButton.IsEnabled = selected && !running && !busy;
    }

    /// <summary>Applies one VmService.StatsReceived push to the matching
    /// VmListItem - CPU%/memory/uptime/guest detail for either engine, plus
    /// MIPS/instruction count when the push came with a riscv-emu sample
    /// (see VmStats's own remarks on why that is not every single tick).
    /// Already marshaled onto the UI thread by the caller.</summary>
    private void ApplyStats(string vmId, VmStats stats)
    {
        var item = _items.FirstOrDefault(i => i.Config.Id == vmId);
        if (item == null) return;
        item.CpuPercent = stats.CpuPercent;
        item.MemoryBytes = stats.MemoryBytes;
        item.Uptime = stats.Uptime;
        item.GuestDetail = stats.GuestDetail;
        item.Mips = stats.Mips;
        item.Instructions = stats.Instructions;

        // Live update for the detail page's own Performance graph, but
        // only while it is actually showing this specific VM - redrawing
        // a graph nobody can see on every ~2s tick for every VM would
        // just be wasted work. The console preview needs no equivalent
        // call here - ConsolePreviewTerminal streams live on its own via
        // OutputReceived once Attach wires it, independent of this timer.
        if (vmId == _detailVmId) RedrawCpuHistory(vmId);
    }

    // Which VM (if any) the detail page is currently showing - set by
    // NavigatorTree_SelectedItemChanged, read by ApplyStats above to
    // decide whether a live update needs the Performance graph/Console
    // preview redrawn right now.
    private string? _detailVmId;

    private void RedrawCpuHistory(string vmId)
    {
        var history = _vmService.GetCpuHistory(vmId);
        var width = CpuHistoryCanvas.ActualWidth;
        var height = CpuHistoryCanvas.ActualHeight;
        if (width <= 0 || height <= 0 || history.Count < 2)
        {
            CpuHistoryLine.Points = new PointCollection();
            CpuHistoryMaxLabel.Text = "-";
            CpuHistoryStartLabel.Text = "";
            CpuHistoryEndLabel.Text = "";
            return;
        }
        var minTime = history[0].TimestampUtc;
        var maxTime = history[^1].TimestampUtc;
        var spanSeconds = (maxTime - minTime).TotalSeconds;
        // The Y-axis top is the actual peak of this sample set, not a
        // fixed 100% - floored at 1% so a VM sitting at ~0% the whole
        // time still gets a sane, non-zero scale instead of a
        // divide-by-zero flat line. Clamped to 100 at the high end:
        // CpuPercent is already per-core-normalized (see VmListItem's
        // own remarks), so a genuine excursion above 100% is not
        // expected, and a transient spike should not alone decide the
        // whole axis if it ever does happen.
        var maxCpu = Math.Clamp(history.Max(s => s.CpuPercent), 1.0, 100.0);
        var points = new PointCollection(history.Count);
        foreach (var sample in history)
        {
            var x = spanSeconds > 0 ? (sample.TimestampUtc - minTime).TotalSeconds / spanSeconds * width : width;
            var clampedCpu = Math.Clamp(sample.CpuPercent, 0, maxCpu);
            var y = height - clampedCpu / maxCpu * height;
            points.Add(new Point(x, y));
        }
        CpuHistoryLine.Points = points;
        CpuHistoryMaxLabel.Text = $"{maxCpu:0.#}%";
        CpuHistoryStartLabel.Text = minTime.ToLocalTime().ToString("HH:mm:ss");
        CpuHistoryEndLabel.Text = maxTime.ToLocalTime().ToString("HH:mm:ss");
    }

    /// <summary>Covers the one gap RedrawCpuHistory's own call sites
    /// cannot: the very first time CpuHistoryCanvas is laid out, its
    /// ActualWidth/Height are still 0 at the moment the detail page's
    /// own selection-changed handler calls RedrawCpuHistory, so that
    /// call draws nothing. Once a real size arrives, redraw with
    /// whatever VM is currently showing.</summary>
    private void CpuHistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_detailVmId != null) RedrawCpuHistory(_detailVmId);
    }

    /// <summary>Opens the full console for whichever VM the detail page
    /// is currently showing - same idempotent path OpenConsoleWindow
    /// already gives the Console button.</summary>
    private void ConsolePreview_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        OpenConsoleWindow(item.Config);
    }

    /// <summary>Phase 4: Recent Tasks is now VmService's own persisted
    /// history (see ITaskHistory), not this window's free-text feed - this
    /// just re-reads it and rebinds, on TasksChanged firing (any task
    /// began, completed, or was recorded) and once at startup. A full
    /// rebind rather than patching one row of an already-bound collection
    /// in place: at most 200 rows, cheap enough that there is no real
    /// reason to build the incremental-update machinery that would avoid
    /// it.</summary>
    private void RefreshRecentTasks() => RecentTasksList.ItemsSource = _vmService.GetRecentTasks();

    private void RecentTasksToggleButton_Click(object sender, RoutedEventArgs e)
    {
        var collapsing = RecentTasksList.Visibility == Visibility.Visible;
        if (collapsing)
        {
            _recentTasksExpandedHeight = RecentTasksRow.ActualHeight > 0 ? RecentTasksRow.ActualHeight : _recentTasksExpandedHeight;
            RecentTasksColumnHeader.Visibility = Visibility.Collapsed;
            RecentTasksList.Visibility = Visibility.Collapsed;
            RecentTasksSplitter.IsEnabled = false;
            RecentTasksSplitter.Visibility = Visibility.Collapsed;
            RecentTasksRow.Height = GridLength.Auto;
        }
        else
        {
            RecentTasksColumnHeader.Visibility = Visibility.Visible;
            RecentTasksList.Visibility = Visibility.Visible;
            RecentTasksSplitter.IsEnabled = true;
            RecentTasksSplitter.Visibility = Visibility.Visible;
            RecentTasksRow.Height = new GridLength(_recentTasksExpandedHeight);
        }
        RecentTasksToggleButton.Content = collapsing ? "Show" : "Hide";
    }

    private void NavigatorTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A VM row under "Virtual Machines" is a VmListItem, not a
        // TreeViewItem - selecting one shows the same VM page the
        // "Virtual Machines" node itself shows (a per-VM detail page is
        // Phase 3's job, not Phase 2's). Every other node carries its page
        // identity in Tag, not Header - Header is now icon+text markup, not
        // a plain string.
        var tag = (e.NewValue as TreeViewItem)?.Tag as string;
        var selectedVm = e.NewValue as VmListItem;

        VirtualMachinesPage.Visibility = tag == "VirtualMachines" ? Visibility.Visible : Visibility.Collapsed;
        // A specific VM's own node: its detail page (Phase 3), bound live
        // via DataContext rather than a one-shot copy of its current
        // values - see VmDetailPage's own remarks in MainWindow.xaml.
        // VmList.SelectedItem is also set to it (even though the grid
        // itself is not visible right now) so the toolbar's shared Click
        // handlers - which all read from SelectedItem, not from sender -
        // and RefreshToolbarState both act on the VM this page is
        // actually showing.
        VmDetailPage.Visibility = selectedVm != null ? Visibility.Visible : Visibility.Collapsed;
        _detailVmId = selectedVm?.Config.Id;
        // Always detached first - leaving a VM's detail page (or
        // switching to a different VM) must stop that VM's own
        // OutputReceived subscription, not leave ConsolePreviewTerminal
        // silently still wired to whatever it last showed.
        ConsolePreviewTerminal.Detach();
        if (selectedVm != null)
        {
            VmDetailPage.DataContext = selectedVm;
            VmList.SelectedItem = selectedVm;
            // An immediate draw, so switching to an already-running VM's
            // detail page shows its graph right away rather than waiting
            // for the next ~2s StatsReceived tick - best-effort only
            // (CpuHistoryCanvas.ActualWidth can still be 0 here if this is
            // the very first time VmDetailPage has ever been laid out;
            // CpuHistoryCanvas_SizeChanged below covers that case, and the
            // next stats tick covers everything else). The console
            // preview needs no equivalent "redraw now" call - Attach
            // itself seeds it from GetConsoleBuffer immediately, then
            // OutputReceived keeps it live on its own, not tied to the
            // ~2s stats cadence at all.
            RedrawCpuHistory(selectedVm.Config.Id);
            ConsolePreviewTerminal.Attach(_vmService, selectedVm.Config.Id, _vmService.GetConsoleBuffer(selectedVm.Config.Id), selectedVm.Config.Debug.EnterSends);
        }
        HostPage.Visibility = tag == "Host" ? Visibility.Visible : Visibility.Collapsed;
        EnginesPage.Visibility = tag == "Engines" ? Visibility.Visible : Visibility.Collapsed;
        StorageDiskPage.Visibility = tag == "Storage" ? Visibility.Visible : Visibility.Collapsed;
        NetworkingPage.Visibility = tag == "Networking" ? Visibility.Visible : Visibility.Collapsed;
        // Each re-read fresh on arrival rather than kept continuously live
        // in the background - see each page's own refresh method for why.
        if (tag == "Host") RefreshHostPage();
        else if (tag == "Engines") _ = RefreshEnginesPageAsync();
        else if (tag == "Storage") _ = RefreshStoragePageAsync();
        else if (tag == "Networking") RefreshNetworkingPage();
        RefreshToolbarState();
    }

    /// <summary>Returns from a VM's detail page to the grid - re-selecting
    /// the "Virtual Machines" node itself is enough: TreeView's own
    /// SelectedItemChanged does the rest via the handler above, the same
    /// path any other sidebar click already takes.</summary>
    private void DetailBackButton_Click(object sender, RoutedEventArgs e) => VirtualMachinesNode.IsSelected = true;
}

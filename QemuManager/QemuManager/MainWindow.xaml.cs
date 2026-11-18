using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using QemuManager.Models;
using QemuManager.Services;
using MessageBox = System.Windows.MessageBox;

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
    private readonly ObservableCollection<string> _recentTasks = new();
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
        RecentTasksList.ItemsSource = _recentTasks;
        HostNameText.Text = Environment.MachineName;
        // Set here, not via IsSelected="True" in XAML: a TreeViewItem fires
        // SelectedItemChanged synchronously as soon as its IsSelected setter
        // runs, which for an XAML-declared value happens mid-InitializeComponent
        // - before VirtualMachinesPage/HostPage/etc. further down in the same
        // document have been constructed yet, so the handler's very first
        // access of one of them threw a NullReferenceException and crashed
        // the app before its window ever appeared. Both fields exist by now.
        VirtualMachinesNode.IsSelected = true;

        _settings = AppSettings.Load();
        _vmService.StateChanged += () => Dispatcher.Invoke(RefreshRunStates);
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
        CheckEngineDirs();
        RefreshToolbarState();
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

    // Was folded into the old blue status bar via SetStatus - moved to the
    // Engines page instead (see MainWindow.xaml's EnginesInfoText): knowing
    // where QEMU/riscv-emu were found is reference info about those
    // engines, not a thing this app *did*, so it does not belong in Recent
    // Tasks.
    private void CheckEngineDirs()
    {
        var parts = new List<string>();
        parts.Add(QemuLocator.LooksLikeQemuDir(_settings.QemuDir)
            ? $"QEMU: {_settings.QemuDir} ({QemuLocator.ListArchitectures(_settings.QemuDir).Count} architectures)."
            : $"No QEMU installation found at '{_settings.QemuDir}'.");
        parts.Add(string.IsNullOrWhiteSpace(_settings.RiscvEmuDir)
            ? "riscv-emu: not configured."
            : RiscvEmuLocator.LooksLikeRiscvEmuDir(_settings.RiscvEmuDir)
                ? $"riscv-emu: {_settings.RiscvEmuDir}."
                : $"riscv-emu not found at '{_settings.RiscvEmuDir}'.");
        EnginesInfoText.Text = string.Join("\n", parts) + "\nOpen Settings to change either.";
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

    private void VmList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (_vmService.IsRunning(item.Config)) { SetStatus("Stop the VM before editing its configuration."); return; }
        EditButton_Click(sender, e);
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
        var kindDialog = new NewVmKindWindow { Owner = this };
        if (kindDialog.ShowDialog() != true) return;

        var vm = _vmService.CreateDraft(kindDialog.SelectedKind);
        // Which edit window to show is a UI-only decision (RiscvEmuEditWindow
        // and VmEditWindow are both WPF Window types, so this can never move
        // into Core) - not an engine-operation branch like the ones this
        // refactor removed.
        bool? result = kindDialog.SelectedKind == "RiscvEmu"
            ? new RiscvEmuEditWindow(vm) { Owner = this }.ShowDialog()
            : new VmEditWindow(vm, _settings.QemuDir) { Owner = this }.ShowDialog();

        if (result == true)
        {
            _vmService.Save(vm);
            _items.Add(new VmListItem(vm, _vmService));
            RefreshSummary();
            SetStatus($"Created VM '{vm.Name}'.");
        }
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (_vmService.IsRunning(item.Config)) { SetStatus("Stop the VM before editing its configuration."); return; }

        bool? result = item.Config.Kind == "RiscvEmu"
            ? new RiscvEmuEditWindow(item.Config) { Owner = this }.ShowDialog()
            : new VmEditWindow(item.Config, _settings.QemuDir) { Owner = this }.ShowDialog();

        if (result == true)
        {
            _vmService.Save(item.Config);
            item.RefreshFromConfig();
            SetStatus($"Saved VM '{item.Config.Name}'.");
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;
        if (_vmService.IsRunning(item.Config)) { SetStatus("Stop the VM before deleting it."); return; }

        var result = MessageBox.Show(this, $"Delete '{item.Config.Name}'? This removes its configuration, not any disk image.",
            "Delete VM", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _vmService.Delete(item.Config);
        _items.Remove(item);
        RefreshSummary();
        SetStatus($"Deleted VM '{item.Config.Name}'.");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        SetStatus($"Starting '{item.Config.Name}'...");
        // StartAsync claims VmService's own per-VM "action in progress"
        // guard synchronously, before its first await - by the time this
        // line returns a Task to await, IsActionInProgress is already
        // true, so refreshing the toolbar right here (not only after the
        // await completes) is what actually disables the button for the
        // duration of the call, closing the double-click race a disabled-
        // only-after-the-fact button would not.
        var startTask = _vmService.StartAsync(item.Config, _settings);
        RefreshToolbarState();
        var message = await startTask;
        SetStatus($"{item.Config.Name}: {message}");

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

        var shutdownTask = _vmService.ShutdownAsync(item.Config);
        RefreshToolbarState(); // see StartButton_Click's own remarks
        var message = await shutdownTask;
        SetStatus($"{item.Config.Name}: {message}");
        RefreshRunStates();
    }

    private void ForceStopButton_Click(object sender, RoutedEventArgs e)
    {
        var item = SelectedItem;
        if (item == null) return;

        var result = MessageBox.Show(this, $"Force-terminate '{item.Config.Name}'? This is equivalent to pulling the power cord.",
            "Force Stop", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        var message = _vmService.ForceStop(item.Config);
        SetStatus($"{item.Config.Name}: {message}");
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
                SetStatus(detail != null
                    ? $"{item.Config.Name}: VNC at {detail} - connect with any VNC client."
                    : $"{item.Config.Name}: VNC endpoint not available.");
                break;
            case ConsoleAvailability.ExternalWindow:
                SetStatus("This VM's Display is gtk/sdl - it shows its own window, not a console here. Edit the VM and set Display to 'console' to change that.");
                break;
        }
    }

    /// <summary>Idempotent: if a console for this VM is already open,
    /// activates it instead of opening a second one. _consoleWindows alone
    /// (a plain WPF dictionary, check-then-assign on the UI thread) should
    /// already guarantee that by itself - TryBeginConsoleSession is a real
    /// second, Core-level guard underneath it, not a cosmetic one; see
    /// VmService's own remarks for why it exists regardless.</summary>
    private void OpenConsoleWindow(VmConfig vm)
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
        var dialog = new SettingsWindow(_settings.QemuDir, _settings.RiscvEmuDir) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _settings.QemuDir = dialog.QemuDir;
            _settings.RiscvEmuDir = dialog.RiscvEmuDir;
            _settings.Save();
            CheckEngineDirs();
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
        var running = _items.Count(i => i.IsRunning);
        // The navigator card this used to live in is gone (Phase 1 fix) -
        // the VM count moves to a small badge on the node itself, and the
        // running/total summary moves to the Host page, where ESXi/Proxmox
        // would put host-level totals too.
        VmCountBadge.Text = _items.Count.ToString();
        HostSummaryText.Text = $"{running} running / {_items.Count} total virtual machine{(_items.Count == 1 ? "" : "s")}.";
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

        StartButton.IsEnabled = CtxStartItem.IsEnabled = DetailStartButton.IsEnabled = selected && !running && !busy;
        StopButton.IsEnabled = CtxStopItem.IsEnabled = DetailStopButton.IsEnabled = selected && running && !busy;
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
    }

    // Phase 1 replaced the single-line status bar with a "Recent tasks"
    // pane. This is still the same one-line-per-action feed every existing
    // SetStatus call site already produces - not yet the Task/Target/
    // Started/Result row schema Phase 4 is scoped to build, which needs
    // each action to report its own start and end separately rather than
    // one fire-and-forget string (the column header above the list is
    // already in place for it - see MainWindow.xaml's RecentTasksColumnHeader).
    private void SetStatus(string text) => _recentTasks.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");

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
        if (selectedVm != null)
        {
            VmDetailPage.DataContext = selectedVm;
            VmList.SelectedItem = selectedVm;
        }
        HostPage.Visibility = tag == "Host" ? Visibility.Visible : Visibility.Collapsed;
        EnginesPage.Visibility = tag == "Engines" ? Visibility.Visible : Visibility.Collapsed;
        var isGenericPlaceholder = tag is "Storage" or "Networking";
        PlaceholderPage.Visibility = isGenericPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        if (isGenericPlaceholder) PlaceholderTitle.Text = tag!;
        RefreshToolbarState();
    }

    /// <summary>Returns from a VM's detail page to the grid - re-selecting
    /// the "Virtual Machines" node itself is enough: TreeView's own
    /// SelectedItemChanged does the rest via the handler above, the same
    /// path any other sidebar click already takes.</summary>
    private void DetailBackButton_Click(object sender, RoutedEventArgs e) => VirtualMachinesNode.IsSelected = true;
}

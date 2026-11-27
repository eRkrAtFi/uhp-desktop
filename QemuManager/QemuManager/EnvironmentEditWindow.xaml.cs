using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using QemuManager.Models;
using QemuManager.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace QemuManager;

/// <summary>
/// One Environment editor, rendered from IEngine.GetConfigSchema instead
/// of VmEditWindow/RiscvEmuEditWindow's own hand-laid-out, per-engine
/// XAML (vision/VISION.md's roadmap item 2: "one schema-driven editor
/// instead of separate per-engine windows"). Works for any engine's
/// schema without this class knowing which engine it is beyond asking
/// IVmService for that engine's own GetConfigSchema/ListArchitectures/
/// Capabilities - a third engine registered later needs no changes here.
///
/// A field whose ConfigField.DependsOn is set (e.g. Platform.MachineType
/// depending on Platform.Architecture) reacts live to the field it
/// depends on changing - see OnFieldChanged - instead of that
/// relationship being hard-coded here per field the way the old
/// VmEditWindow's own ArchHints dictionary was; a schema the editor has
/// never seen before works the same way with no changes to this file.
/// </summary>
public partial class EnvironmentEditWindow : Window
{
    private readonly EnvironmentConfig _env;
    private readonly IVmService _vmService;
    private readonly AppSettings _settings;

    private sealed class FieldRow
    {
        public required ConfigField Field { get; init; }
        public required Func<object?> Read { get; init; }
        public required Action<object?> Write { get; init; }
        public TextBlock? HintBlock { get; set; }
    }

    // Keyed by ConfigField.Key, not a list - DependsOn resolves the
    // field it depends on by that same key, from whichever row is
    // already built. A dependent field's schema entry must come after
    // the field it depends on (true for every engine's schema today -
    // Platform.Architecture is always listed before Platform.MachineType).
    private readonly Dictionary<string, FieldRow> _rowsByKey = new();

    public EnvironmentEditWindow(EnvironmentConfig env, AppSettings settings, IVmService vmService)
    {
        InitializeComponent();
        _env = env;
        _settings = settings;
        _vmService = vmService;

        Title = $"{_vmService.GetHardwareDescription(env).EngineDisplayName} Environment Configuration";
        NameBox.Text = env.Name;

        var schema = _vmService.GetConfigSchema(env.EngineId);
        // Fixed order (EnvironmentConfig's own sub-object order), not
        // whatever order this engine's own schema happens to list fields
        // in - every engine's editor should read the same way, section
        // for section, rather than each engine's own GetConfigSchema
        // implicitly deciding the layout. A group with no fields for this
        // engine (e.g. riscv-emu has no Firmware fields) is skipped
        // entirely, not shown as an empty heading.
        foreach (var group in CanonicalGroupOrder)
        {
            // VisibleWhen filtered here, not inside AddFieldRow - so a
            // group left with nothing visible (not possible for any
            // schema today, but not assumed either) is skipped entirely
            // rather than showing an empty heading.
            var fieldsInGroup = schema.Where(f => f.Group == group && (f.VisibleWhen?.Invoke(env) ?? true)).ToList();
            if (fieldsInGroup.Count == 0) continue;

            FieldsPanel.Children.Add(new TextBlock { Text = group, Style = (Style)FindResource("GroupHeading") });
            foreach (var field in fieldsInGroup)
                AddFieldRow(field);
        }
    }

    private static readonly string[] CanonicalGroupOrder = { "Platform", "Firmware", "Workload", "Debug" };

    private void AddFieldRow(ConfigField field)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock { Text = field.Label, Style = (Style)FindResource("FieldLabel") };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var valueColumn = new StackPanel();
        Grid.SetColumn(valueColumn, 1);
        grid.Children.Add(valueColumn);

        var (read, write) = field.Type switch
        {
            FieldType.Bool => AddBoolControl(valueColumn, field),
            FieldType.Choice => AddChoiceControl(valueColumn, field),
            FieldType.FilePath => AddFilePathControl(valueColumn, field),
            FieldType.MultilineText => AddTextControl(valueColumn, field, multiline: true),
            FieldType.Int => AddTextControl(valueColumn, field, multiline: false),
            _ => AddTextControl(valueColumn, field, multiline: false),
        };

        // A Bool field's own hint is already the checkbox's own label (see
        // AddBoolControl) - repeating it as a second line right below
        // would just show the same sentence twice.
        TextBlock? hintBlock = null;
        if (field.Type != FieldType.Bool)
        {
            var initialHint = ResolveHint(field);
            if (!string.IsNullOrEmpty(initialHint))
            {
                hintBlock = new TextBlock { Text = initialHint, Style = (Style)FindResource("HintText") };
                valueColumn.Children.Add(hintBlock);
            }
        }

        _rowsByKey[field.Key] = new FieldRow { Field = field, Read = read, Write = write, HintBlock = hintBlock };

        FieldsPanel.Children.Add(grid);
    }

    /// <summary>A DependsOn.Hint result overrides the field's own static
    /// Hint for whatever the depended-on field's CURRENT value is (null
    /// falls back to the static Hint) - resolved against the live
    /// control if that field's row already exists, otherwise (should not
    /// happen for any schema today) just the static Hint.</summary>
    private string? ResolveHint(ConfigField field)
    {
        if (field.DependsOn?.Hint is { } hintFor && _rowsByKey.TryGetValue(field.DependsOn.OnKey, out var onRow))
            return hintFor(onRow.Read()) ?? field.Hint;
        return field.Hint;
    }

    /// <summary>Called whenever a field's own control changes value -
    /// updates every OTHER field whose ConfigField.DependsOn.OnKey is
    /// this one: its hint text (always), and - only if that field is
    /// currently empty - fills in DependsOn.SuggestedDefault. Never
    /// overwrites a value the user (or the loaded Environment) already
    /// set, so this only ever offers a default into a field nobody has
    /// touched yet.</summary>
    private void OnFieldChanged(string key, object? newValue)
    {
        foreach (var row in _rowsByKey.Values)
        {
            var dep = row.Field.DependsOn;
            if (dep == null || dep.OnKey != key) continue;

            if (dep.Hint != null && row.HintBlock != null)
                row.HintBlock.Text = dep.Hint(newValue) ?? row.Field.Hint ?? "";

            if (dep.SuggestedDefault != null)
            {
                var current = row.Read();
                var isEmpty = current is null || (current is string s && string.IsNullOrWhiteSpace(s));
                if (isEmpty)
                {
                    var suggested = dep.SuggestedDefault(newValue);
                    if (suggested != null) row.Write(suggested);
                }
            }
        }
    }

    /// <summary>A field's own static Required flag, OR'd with
    /// DependsOn.IsRequired evaluated against whatever the depended-on
    /// field's control currently shows (not a snapshot of _env, so
    /// changing Architecture and then immediately hitting Save sees the
    /// just-changed value) and with RequiredWhen evaluated against _env
    /// directly (Workload.Kind, fixed for this editing session - see
    /// ConfigField.RequiredWhen's own remarks).</summary>
    private bool IsRequiredNow(ConfigField field)
    {
        if (field.Required) return true;
        if (field.RequiredWhen?.Invoke(_env) == true) return true;
        if (field.DependsOn?.IsRequired is { } isRequired && _rowsByKey.TryGetValue(field.DependsOn.OnKey, out var onRow))
            return isRequired(onRow.Read());
        return false;
    }

    private (Func<object?> Read, Action<object?> Write) AddTextControl(StackPanel column, ConfigField field, bool multiline)
    {
        var box = new TextBox
        {
            Text = field.Get(_env)?.ToString() ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 60 : double.NaN,
        };
        column.Children.Add(box);
        Func<object?> read = field.Type == FieldType.Int
            ? () => int.TryParse(box.Text.Trim(), out var n) ? n : (int?)null
            : () => box.Text.Trim();
        Action<object?> write = v => box.Text = v?.ToString() ?? "";
        return (read, write);
    }

    private (Func<object?> Read, Action<object?> Write) AddBoolControl(StackPanel column, ConfigField field)
    {
        var check = new CheckBox
        {
            IsChecked = field.Get(_env) is true,
            // A plain string Content never wraps inside a CheckBox's own
            // template (confirmed directly: a long hint like Debug.GdbEnabled's
            // own just got clipped at the dialog's own edge) - a TextBlock
            // does, as long as it is actually allowed to be narrower than
            // its own text; VerticalAlignment=Center keeps it lined up with
            // the checkbox glyph the same way the single-line case already
            // was.
            Content = new TextBlock
            {
                Text = field.Hint ?? "",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF1, 0xF1)),
        };
        // The hint is shown as the checkbox's own label here instead of a
        // separate line below it - AddFieldRow's own hint line would
        // otherwise be redundant right under a checkbox that already
        // carries the same text.
        column.Children.Add(check);
        return (() => check.IsChecked == true, v => check.IsChecked = v is true);
    }

    private (Func<object?> Read, Action<object?> Write) AddChoiceControl(StackPanel column, ConfigField field)
    {
        var combo = new ComboBox();
        // Platform.Architecture's own options are runtime-dependent
        // (whatever qemu-system-*.exe binaries settings.QemuDir actually
        // has) rather than the fixed list every other Choice field
        // declares in its own schema - see ConfigField.Choices' own
        // remarks.
        var choices = (field.Key == "Platform.Architecture"
            ? _vmService.ListArchitectures(_env.EngineId, _settings)
            : field.Choices ?? Array.Empty<string>()).ToList();
        foreach (var choice in choices) combo.Items.Add(choice);

        var current = field.Get(_env)?.ToString();
        var index = choices.FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;

        column.Children.Add(combo);

        // Generic, not special-cased to Platform.Architecture - any
        // Choice field other fields depend on reacts the same way.
        combo.SelectionChanged += (_, _) => OnFieldChanged(field.Key, combo.SelectedItem as string);

        Action<object?> write = v =>
        {
            var target = v as string;
            var idx = -1;
            for (var i = 0; i < combo.Items.Count; i++)
                if (string.Equals(combo.Items[i] as string, target, StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
            if (idx >= 0) combo.SelectedIndex = idx;
        };
        return (() => combo.SelectedItem as string, write);
    }

    private (Func<object?> Read, Action<object?> Write) AddFilePathControl(StackPanel column, ConfigField field)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox { Text = field.Get(_env)?.ToString() ?? "" };
        Grid.SetColumn(box, 0);
        row.Children.Add(box);

        var browse = new Button { Content = "Browse..." };
        Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            var dialog = new OpenFileDialog { Filter = field.FileFilter ?? "All files (*.*)|*.*", FileName = box.Text };
            if (dialog.ShowDialog() == true) box.Text = dialog.FileName;
        };
        row.Children.Add(browse);

        column.Children.Add(row);

        // Phase 6: creating a new disk moved to the Storage page (see
        // MainWindow.xaml's CreateDiskButton) - this field only points
        // there now rather than owning its own second copy of that same
        // CreateDiskWindow dialog. Only for Workload.DiskPath, and only
        // for an engine whose disk is a real qcow2 QEMU manages itself -
        // riscv-emu's own disk is a plain raw image with no "create"
        // flow on either page.
        if (field.Key == "Workload.DiskPath" && _env.EngineId == "Qemu")
        {
            var hint = new TextBlock
            {
                Text = "To create a new disk, use the Storage page.",
                FontSize = 11, Opacity = 0.7, Margin = new Thickness(0, 2, 0, 0),
            };
            column.Children.Add(hint);
        }

        return (() => box.Text.Trim(), v => box.Text = v as string ?? "");
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationText.Text = "Name is required."; return; }

        // Validate every field before writing any of them back - a
        // partially-applied save (some fields written, then an error on a
        // later one) would leave _env in a worse state than either
        // "unchanged" or "fully updated."
        foreach (var row in _rowsByKey.Values)
        {
            var value = row.Read();
            if (IsRequiredNow(row.Field) && (value is null || (value is string s && string.IsNullOrWhiteSpace(s))))
            {
                ValidationText.Text = $"{row.Field.Label} is required.";
                return;
            }
            if (row.Field.Type == FieldType.Int && value is null)
            {
                ValidationText.Text = $"{row.Field.Label} must be a whole number.";
                return;
            }
            var error = row.Field.Validate?.Invoke(value);
            if (error != null) { ValidationText.Text = error; return; }
        }

        _env.Name = name;
        foreach (var row in _rowsByKey.Values) row.Field.Set(_env, row.Read());

        // Non-blocking - the Environment is still saved either way, same
        // as SettingsWindow's own identical check for the same reason (a
        // user may well be preparing one before QEMU is even installed
        // yet). Only meaningful for QEMU - riscv-emu's own "is it there"
        // check already happens at Power On time via a clearer, engine-
        // specific message.
        if (_env.EngineId == "Qemu" && !QemuLocator.LooksLikeQemuDir(_settings.QemuDir))
        {
            MessageBox.Show(this,
                $"No qemu-system-*.exe found in \"{_settings.QemuDir}\".\n\nThis Environment will be saved, but Power On will fail until QEMU is installed and Settings points at it.",
                "QEMU not found", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

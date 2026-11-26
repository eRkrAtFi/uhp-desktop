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
/// </summary>
public partial class EnvironmentEditWindow : Window
{
    private readonly EnvironmentConfig _env;
    private readonly IVmService _vmService;
    private readonly AppSettings _settings;
    private readonly List<(ConfigField Field, Func<object?> Read)> _rows = new();

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
            var fieldsInGroup = schema.Where(f => f.Group == group).ToList();
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

        Func<object?> read = field.Type switch
        {
            FieldType.Bool => AddBoolControl(valueColumn, field),
            FieldType.Choice => AddChoiceControl(valueColumn, field),
            FieldType.FilePath => AddFilePathControl(valueColumn, field),
            FieldType.MultilineText => AddTextControl(valueColumn, field, multiline: true),
            FieldType.Int => AddTextControl(valueColumn, field, multiline: false),
            _ => AddTextControl(valueColumn, field, multiline: false),
        };
        _rows.Add((field, read));

        // A Bool field's own hint is already the checkbox's own label (see
        // AddBoolControl) - repeating it as a second line right below
        // would just show the same sentence twice.
        if (!string.IsNullOrEmpty(field.Hint) && field.Type != FieldType.Bool)
            valueColumn.Children.Add(new TextBlock { Text = field.Hint, Style = (Style)FindResource("HintText") });

        FieldsPanel.Children.Add(grid);
    }

    private Func<object?> AddTextControl(StackPanel column, ConfigField field, bool multiline)
    {
        var box = new TextBox
        {
            Text = field.Get(_env)?.ToString() ?? "",
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 60 : double.NaN,
        };
        column.Children.Add(box);
        return field.Type == FieldType.Int
            ? () => int.TryParse(box.Text.Trim(), out var n) ? n : (int?)null
            : () => box.Text.Trim();
    }

    private Func<object?> AddBoolControl(StackPanel column, ConfigField field)
    {
        var check = new CheckBox
        {
            IsChecked = field.Get(_env) is true,
            Content = field.Hint ?? "",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF1, 0xF1, 0xF1)),
        };
        // The hint is shown as the checkbox's own label here instead of a
        // separate line below it - AddFieldRow's own hint line would
        // otherwise be redundant right under a checkbox that already
        // carries the same text.
        column.Children.Add(check);
        return () => check.IsChecked == true;
    }

    private Func<object?> AddChoiceControl(StackPanel column, ConfigField field)
    {
        var combo = new ComboBox();
        // Platform.Architecture's own options are runtime-dependent
        // (whatever qemu-system-*.exe binaries settings.QemuDir actually
        // has) rather than the fixed list every other Choice field
        // declares in its own schema - see ConfigField.Choices' own
        // remarks.
        var choices = field.Key == "Platform.Architecture"
            ? _vmService.ListArchitectures(_env.EngineId, _settings)
            : field.Choices ?? Array.Empty<string>();
        foreach (var choice in choices) combo.Items.Add(choice);

        var current = field.Get(_env)?.ToString();
        var index = choices.ToList().FindIndex(c => string.Equals(c, current, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = index >= 0 ? index : 0;

        column.Children.Add(combo);
        return () => combo.SelectedItem as string;
    }

    private Func<object?> AddFilePathControl(StackPanel column, ConfigField field)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Only Workload.DiskPath, and only for an engine whose disk is a
        // real qcow2 QEMU manages itself (DiskImageService.CreateQcow2Async
        // shells out to qemu-img.exe) - riscv-emu's own disk is a plain
        // raw image with nothing here to create it the same way.
        var showNewButton = field.Key == "Workload.DiskPath" && _env.EngineId == "Qemu";
        if (showNewButton) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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

        if (showNewButton)
        {
            var newButton = new Button { Content = "New..." };
            Grid.SetColumn(newButton, 2);
            newButton.Click += (_, _) =>
            {
                var suggestedName = string.IsNullOrWhiteSpace(NameBox.Text) ? "disk" : NameBox.Text;
                var suggestedPath = Path.Combine(AppPaths.AppDataDir, "disks", suggestedName + ".qcow2");
                Directory.CreateDirectory(Path.GetDirectoryName(suggestedPath)!);
                var dialog = new CreateDiskWindow(_settings.QemuDir, suggestedPath, _vmService) { Owner = this };
                if (dialog.ShowDialog() == true) box.Text = dialog.CreatedPath;
            };
            row.Children.Add(newButton);
        }

        column.Children.Add(row);
        return () => box.Text.Trim();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { ValidationText.Text = "Name is required."; return; }

        // Validate every field before writing any of them back - a
        // partially-applied save (some fields written, then an error on a
        // later one) would leave _env in a worse state than either
        // "unchanged" or "fully updated."
        foreach (var (field, read) in _rows)
        {
            var value = read();
            if (field.Required && (value is null || (value is string s && string.IsNullOrWhiteSpace(s))))
            {
                ValidationText.Text = $"{field.Label} is required.";
                return;
            }
            if (field.Type == FieldType.Int && value is null)
            {
                ValidationText.Text = $"{field.Label} must be a whole number.";
                return;
            }
            var error = field.Validate?.Invoke(value);
            if (error != null) { ValidationText.Text = error; return; }
        }

        _env.Name = name;
        foreach (var (field, read) in _rows) field.Set(_env, read());

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

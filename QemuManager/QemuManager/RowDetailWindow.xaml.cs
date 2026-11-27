using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;

namespace QemuManager;

public partial class RowDetailWindow : Window
{
    private readonly List<(string Label, string Value)> _fields;

    public RowDetailWindow(string title, IReadOnlyList<(string Label, string Value)> fields)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        _fields = new List<(string, string)>(fields);

        foreach (var (label, value) in _fields)
        {
            FieldsPanel.Children.Add(new TextBlock
            {
                Text = label, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("TextDim"),
                Margin = new Thickness(0, 10, 0, 2),
            });
            FieldsPanel.Children.Add(new TextBox
            {
                Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true, BorderThickness = new Thickness(1),
            });
        }
    }

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (var (label, value) in _fields) sb.AppendLine($"{label}: {value}");
        Clipboard.SetText(sb.ToString());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

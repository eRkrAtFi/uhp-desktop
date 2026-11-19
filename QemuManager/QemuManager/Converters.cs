using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QemuManager;

/// <summary>The inverse of WPF's own BooleanToVisibilityConverter (true ->
/// Collapsed, false -> Visible) - used for "show this only while the VM
/// is NOT running" (the detail page's own stopped-VM config summary),
/// where binding {Binding IsRunning} directly with the stock converter
/// would show it backwards.</summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

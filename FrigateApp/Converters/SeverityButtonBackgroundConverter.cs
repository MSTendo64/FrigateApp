using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace FrigateApp.Converters;

/// <summary>Для кнопок вкладок Обзор событий: выделенный тип — тёмный фон.</summary>
public sealed class SeverityButtonBackgroundConverter : IValueConverter
{
    public static readonly SeverityButtonBackgroundConverter Instance = new();
    private static readonly SolidColorBrush Selected = new(Color.FromRgb(60, 60, 60));
    private static readonly SolidColorBrush Unselected = new(Colors.Transparent);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value as string ?? "";
        var target = parameter as string ?? "";
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase) ? Selected : Unselected;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

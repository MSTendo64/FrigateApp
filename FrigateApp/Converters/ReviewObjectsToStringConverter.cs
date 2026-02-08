using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;

namespace FrigateApp.Converters;

public sealed class ReviewObjectsToStringConverter : IValueConverter
{
    public static readonly ReviewObjectsToStringConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is List<string> list && list.Count > 0)
            return string.Join(", ", list.Select(x => x.Replace("-verified", "")));
        if (value is IEnumerable<string> en)
            return string.Join(", ", en.Select(x => x.Replace("-verified", "")));
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace FrigateApp.Converters;

/// <summary>
/// Возвращает placeholder-битмап при null или утилизированном Bitmap.
/// Предотвращает NullReferenceException в Image.MeasureOverride.
/// </summary>
public sealed class NullToPlaceholderBitmapConverter : IValueConverter
{
    public static readonly NullToPlaceholderBitmapConverter Instance = new();

    private static readonly Bitmap PlaceholderBitmap = CreatePlaceholder();

    private static Bitmap CreatePlaceholder()
    {
        var wb = new WriteableBitmap(
            new Avalonia.PixelSize(1, 1),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        using (var lockObj = wb.Lock())
        {
            unsafe
            {
                var ptr = (byte*)lockObj.Address;
                ptr[0] = 17;  // B
                ptr[1] = 17;  // G
                ptr[2] = 17;  // R
                ptr[3] = 255; // A
            }
        }
        return wb;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Bitmap bmp)
        {
            // Пытаемся получить размер — если Bitmap утилизирован, будет исключение
            try
            {
                var _ = bmp.PixelSize;
                var __ = bmp.Size;  // Дополнительная проверка
                return bmp;
            }
            catch
            {
                // Bitmap утилизирован — возвращаем placeholder
                return PlaceholderBitmap;
            }
        }
        return PlaceholderBitmap;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value;
}

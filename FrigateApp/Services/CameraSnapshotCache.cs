using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace FrigateApp.Services;

/// <summary>
/// Кэш превью камер (sub-поток) — сохраняет последний кадр и метаданные при переходах между группами.
/// </summary>
public class CameraSnapshotCache
{
    private readonly Dictionary<string, Bitmap?> _cache = new();
    private readonly Dictionary<string, bool> _isVerticalCache = new();
    private readonly object _lock = new();

    /// <summary>Получить закэшированное превью камеры (или null).</summary>
    public Bitmap? Get(string cameraName)
    {
        lock (_lock)
        {
            return _cache.TryGetValue(cameraName, out var bmp) ? bmp : null;
        }
    }

    /// <summary>Сохранить превью камеры в кэш.</summary>
    public void Set(string cameraName, Bitmap? bitmap)
    {
        lock (_lock)
        {
            _cache[cameraName] = bitmap;
        }
    }

    /// <summary>Получить информацию о вертикальности камеры (null если не определена).</summary>
    public bool? GetIsVertical(string cameraName)
    {
        lock (_lock)
        {
            return _isVerticalCache.TryGetValue(cameraName, out var isVert) ? isVert : null;
        }
    }

    /// <summary>Сохранить информацию о вертикальности камеры.</summary>
    public void SetIsVertical(string cameraName, bool isVertical)
    {
        lock (_lock)
        {
            _isVerticalCache[cameraName] = isVertical;
        }
    }

    /// <summary>Очистить весь кэш (при выходе из аккаунта).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var bmp in _cache.Values)
                bmp?.Dispose();
            _cache.Clear();
            _isVerticalCache.Clear();
        }
    }
}

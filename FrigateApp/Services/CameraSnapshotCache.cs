using System.Collections.Concurrent;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace FrigateApp.Services;

/// <summary>
/// Кэш превью камер — сохраняет последний кадр и метаданные при переходах между группами.
/// Поддерживает LRU-вытеснение для работы с 30+ камерами.
/// </summary>
public class CameraSnapshotCache
{
    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new();
    private readonly ConcurrentDictionary<string, bool> _isVerticalCache = new();
    private readonly Queue<string> _lruQueue = new();
    private readonly object _lruLock = new();
    private readonly int _maxCacheSize;

    public CameraSnapshotCache(int maxCacheSize = 50)
    {
        _maxCacheSize = maxCacheSize;
    }

    /// <summary>Получить закэшированное превью камеры (или null).</summary>
    public Bitmap? Get(string cameraName)
    {
        if (!_cache.TryGetValue(cameraName, out var bmp))
            return null;
        
        return bmp;
    }

    /// <summary>Сохранить превью камеры в кэш с LRU-вытеснением.</summary>
    public void Set(string cameraName, Bitmap? bitmap)
    {
        Bitmap? oldBitmapToDispose = null;
        
        // LRU: добавляем в очередь
        lock (_lruLock)
        {
            // Если уже есть — сохраняем старый Bitmap для утилизации
            if (_cache.TryGetValue(cameraName, out var existing))
            {
                oldBitmapToDispose = existing;
                
                // Удаляем из очереди (переместим в конец)
                var temp = new Queue<string>();
                while (_lruQueue.Count > 0)
                {
                    var item = _lruQueue.Dequeue();
                    if (item != cameraName)
                        temp.Enqueue(item);
                }
                foreach (var item in temp)
                    _lruQueue.Enqueue(item);
            }

            _lruQueue.Enqueue(cameraName);

            // Вытесняем старые, если превышен лимит
            while (_lruQueue.Count > _maxCacheSize)
            {
                var oldest = _lruQueue.Dequeue();
                if (_cache.TryRemove(oldest, out var oldBmp))
                {
                    oldBmp?.Dispose();
                }
            }
        }

        // Утилизируем старый Bitmap этой камеры (вне блокировки)
        if (oldBitmapToDispose != null && oldBitmapToDispose != bitmap)
        {
            try { oldBitmapToDispose.Dispose(); } catch { }
        }

        _cache[cameraName] = bitmap;
    }

    /// <summary>Получить информацию о вертикальности камеры (null если не определена).</summary>
    public bool? GetIsVertical(string cameraName)
    {
        _isVerticalCache.TryGetValue(cameraName, out var isVert);
        return isVert;
    }

    /// <summary>Сохранить информацию о вертикальности камеры.</summary>
    public void SetIsVertical(string cameraName, bool isVertical)
    {
        _isVerticalCache[cameraName] = isVertical;
    }

    /// <summary>Очистить весь кэш (при выходе из аккаунта).</summary>
    public void Clear()
    {
        lock (_lruLock)
        {
            foreach (var bmp in _cache.Values)
            {
                bmp?.Dispose();
            }
            _cache.Clear();
            _isVerticalCache.Clear();
            _lruQueue.Clear();
        }
    }
}

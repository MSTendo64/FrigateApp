using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class CameraItemViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private Bitmap? _snapshot;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private double _zoomLevel = 1;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    /// <summary>Зум разрешён только в выбранной группе камер, на виде «Все камеры» — нет.</summary>
    [ObservableProperty]
    private bool _isZoomEnabled;

    /// <summary>Высота плитки камеры (зависит от вертикальности и масштаба).</summary>
    [ObservableProperty]
    private double _tileHeight = 160;

    /// <summary>Камера вертикальная (высота больше ширины).</summary>
    [ObservableProperty]
    private bool _isVertical;

    /// <summary>Масштаб размера плитки (влияет на TileHeight).</summary>
    private double _tileScale = 1.0;

    /// <summary>Поворот камеры в градусах из конфигурации Frigate.</summary>
    [ObservableProperty]
    private float _rotation;

    /// <summary>Строка статистики: поток, задержка, трафик (для отображения при включённой статистике).</summary>
    [ObservableProperty]
    private string _statsText = "";

    private readonly FrigateApiService _api;
    private readonly CameraSnapshotCache? _snapshotCache;
    // Оптимизация: адаптивная частота обновления
    private TimeSpan _refreshInterval = TimeSpan.FromMilliseconds(2000);
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _isVisible = true; // Видимость камеры на экране
    private int _failedAttempts = 0; // Счетчик неудачных попыток
    private DateTime _lastSuccessfulUpdate = DateTime.UtcNow;

    private readonly Action? _onOpenPlayer;
    private readonly Action<CameraItemViewModel>? _onZoomChanged;

    public CameraItemViewModel(
        string cameraName,
        FrigateApiService api,
        Action? onOpenPlayer = null,
        CameraZoomState? initialZoom = null,
        Action<CameraItemViewModel>? onZoomChanged = null,
        bool isZoomEnabled = false,
        CameraSnapshotCache? snapshotCache = null,
        float rotation = 0f,
        double tileScale = 1.0)
    {
        Name = cameraName;
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onOpenPlayer = onOpenPlayer;
        _onZoomChanged = onZoomChanged;
        _snapshotCache = snapshotCache;
        IsZoomEnabled = isZoomEnabled;
        Rotation = rotation;
        _tileScale = tileScale;
        
        // Загрузить закэшированное превью (если есть) — мгновенное отображение при переходах
        if (_snapshotCache != null)
        {
            var cached = _snapshotCache.Get(cameraName);
            if (cached != null)
                Snapshot = cached;
            
            // Загрузить закэшированную информацию о вертикальности
            var cachedIsVertical = _snapshotCache.GetIsVertical(cameraName);
            if (cachedIsVertical.HasValue)
            {
                IsVertical = cachedIsVertical.Value;
                UpdateTileHeight(cachedIsVertical.Value);
            }
        }
        
        if (initialZoom != null && initialZoom.ZoomLevel >= 1)
        {
            _zoomLevel = Math.Clamp(initialZoom.ZoomLevel, 1, 4);
            _panX = initialZoom.PanX;
            _panY = initialZoom.PanY;
        }
    }

    /// <summary>Зум колёсиком относительно курсора (Shift+колесо). При уменьшении до 1 — сброс позиции. Мин = 1, макс = 4.</summary>
    public void ZoomAt(double cursorX, double cursorY, double viewWidth, double viewHeight, double delta)
    {
        if (!IsZoomEnabled) return;
        var factor = delta > 0 ? 1.1 : 1.0 / 1.1;
        var newScale = Math.Clamp(ZoomLevel * factor, 1, 4);
        if (newScale == ZoomLevel) return;
        if (newScale <= 1)
        {
            ZoomLevel = 1;
            PanX = 0;
            PanY = 0;
        }
        else
        {
            PanX = cursorX - (cursorX - PanX) * (newScale / ZoomLevel);
            PanY = cursorY - (cursorY - PanY) * (newScale / ZoomLevel);
            ZoomLevel = newScale;
        }
        _onZoomChanged?.Invoke(this);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenPlayer()
    {
        _onOpenPlayer?.Invoke();
    }

    /// <summary>Установить видимость камеры (для оптимизации обновлений).</summary>
    public void SetVisibility(bool isVisible)
    {
        if (_isVisible == isVisible) return;
        
        _isVisible = isVisible;
        
        // Адаптивная частота обновлений:
        // - Видимые камеры: 2 сек (актуальное превью)
        // - Невидимые камеры: 60 сек (экономия ресурсов, но превью актуально в течение минуты)
        _refreshInterval = isVisible 
            ? TimeSpan.FromMilliseconds(2000) 
            : TimeSpan.FromMilliseconds(60000);
        
        System.Diagnostics.Debug.WriteLine($"[{Name}] Visibility changed: {isVisible}, interval: {_refreshInterval.TotalSeconds}s");
    }

    public void StartRefresh()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = RefreshLoopAsync(ct);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        // Оптимизация: случайная задержка старта (0-500мс) для распределения нагрузки
        // (уменьшено с 2000мс, т.к. теперь есть throttling на уровне API)
        try
        {
            var rnd = new Random();
            await Task.Delay(rnd.Next(0, 500), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                // Адаптивное качество:
                // - Видимые камеры: 480p (хорошее качество для мониторинга)
                // - Невидимые камеры: 360p (достаточно для превью, но легче для сервера)
                var height = _isVisible ? 480 : 360;
                var requestStart = DateTime.UtcNow;
                var bytes = await _api.GetLatestFrameBytesAsync(Name, height, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || _disposed) return;
                if (bytes != null && bytes.Length > 0)
                {
                    try
                    {
                        // Оптимизация: декодирование на пуле потоков с более низким приоритетом
                        var (bmp, origWidth, origHeight) = await DecodeImageAsync(bytes, ct).ConfigureAwait(true);
                        
                        if (ct.IsCancellationRequested || _disposed)
                        {
                            bmp?.Dispose();
                            return;
                        }
                        
                                    // НЕ диспозим старый bitmap сразу — Avalonia может ещё его рендерить
                                    // Пусть GC сам очистит, когда UI больше не использует
                                    Snapshot = bmp;
                                    
                                    // Определяем вертикальность ТОЛЬКО если она еще не сохранена в кеше
                                    var cachedIsVertical = _snapshotCache?.GetIsVertical(Name);
                                    if (!cachedIsVertical.HasValue)
                                    {
                                        // Определяем вертикальность с учетом ОРИГИНАЛЬНЫХ размеров + rotation
                                        var isVerticalNow = IsVerticalOrientation(origWidth, origHeight, Rotation);
                                        
                                        IsVertical = isVerticalNow;
                                        UpdateTileHeight(isVerticalNow);
                                        
                                        // Сохраняем в кеш чтобы не пересчитывать при переходах
                                        _snapshotCache?.SetIsVertical(Name, isVerticalNow);
                                    }
                                    
                                    // Сохранить bitmap в кэш для мгновенного появления при переходе между группами
                                    _snapshotCache?.Set(Name, bmp);
                                    
                                    HasError = false;
                                    ErrorText = "";
                                    _failedAttempts = 0;
                                    _lastSuccessfulUpdate = DateTime.UtcNow;

                                    // Статистика для отображения под плиткой (поток, задержка, трафик)
                                    var delayMs = (int)(DateTime.UtcNow - requestStart).TotalMilliseconds;
                                    var trafficKb = bytes.Length / 1024.0;
                                    StatsText = $"Поток MJPEG • Задержка {delayMs} мс • Трафик {trafficKb:F1} КБ";
                    }
                    catch (Exception ex)
                    {
                        _failedAttempts++;
                        HasError = true;
                        ErrorText = $"Ошибка декодирования ({_failedAttempts})";
                        System.Diagnostics.Debug.WriteLine($"[{Name}] Decode error: {ex.Message}");
                    }
                }
                else
                {
                    _failedAttempts++;
                    HasError = true;
                    ErrorText = $"Нет данных ({_failedAttempts})";
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_disposed)
                {
                    HasError = true;
                    ErrorText = ex.Message;
                }
            }

            try
            {
                await Task.Delay(_refreshInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Оптимизированное декодирование изображения с минимальными аллокациями.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Task<(Bitmap bmp, int origWidth, int origHeight)> DecodeImageAsync(byte[] bytes, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(ms);
            var w = bitmap.PixelSize.Width;
            var h = bitmap.PixelSize.Height;
            
            // Поворачиваем изображение если нужно (кеш поворота в CameraSnapshotCache)
            if (Rotation != 0)
            {
                // Проверяем кеш повернутого изображения
                var rotatedKey = $"{Name}_rotated_{Rotation}";
                var cachedRotated = _snapshotCache?.Get(rotatedKey);
                if (cachedRotated != null && cachedRotated.PixelSize.Width > 0)
                {
                    bitmap.Dispose();
                    return (cachedRotated, w, h);
                }
                
                bitmap = RotateBitmap(bitmap, Rotation);
                _snapshotCache?.Set(rotatedKey, bitmap);
            }
            
            return (bitmap, w, h);
        }, ct);
    }

    public void StopRefresh()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefresh();
        // НЕ диспозим Snapshot — он может быть в CameraSnapshotCache и использоваться другими экземплярами
        // GC сам очистит bitmap когда он больше нигде не используется
        Snapshot = null;
    }

    /// <summary>Обновить масштаб плитки.</summary>
    public void UpdateTileScale(double scale)
    {
        _tileScale = scale;
        UpdateTileHeight(IsVertical);
    }

    /// <summary>Обновить высоту плитки с учетом вертикальности и масштаба.</summary>
    private void UpdateTileHeight(bool isVertical)
    {
        var baseHeight = 160 * _tileScale;
        // Для вертикальных: 2 плитки + margin между ними (TileMargin * 2 = 4)
        TileHeight = isVertical ? (baseHeight * 2 + 4) : baseHeight;
    }

    /// <summary>Определяет, является ли ориентация вертикальной с учетом поворота.</summary>
    private static bool IsVerticalOrientation(int width, int height, float rotation)
    {
        // Нормализуем угол к 0-360
        var angle = rotation % 360;
        if (angle < 0) angle += 360;
        
        // При повороте 90° или 270° меняются местами ширина и высота
        var isRotated90or270 = Math.Abs(angle - 90) < 1 || Math.Abs(angle - 270) < 1;
        
        if (isRotated90or270)
        {
            // Свапнули размеры - проверяем противоположное
            return width > height;
        }
        else
        {
            // Обычная проверка
            return height > width;
        }
    }

    /// <summary>Поворачивает Bitmap на заданный угол.</summary>
    private static Bitmap RotateBitmap(Bitmap source, float angle)
    {
        // Нормализуем угол
        var normalizedAngle = angle % 360;
        if (normalizedAngle < 0) normalizedAngle += 360;
        
        // Для точных углов 90, 180, 270 используем быстрый метод
        if (Math.Abs(normalizedAngle - 90) < 1)
            return RotateBitmap90(source);
        if (Math.Abs(normalizedAngle - 180) < 1)
            return RotateBitmap180(source);
        if (Math.Abs(normalizedAngle - 270) < 1)
            return RotateBitmap270(source);
        
        // Для других углов возвращаем оригинал (сложный поворот требует SkiaSharp)
        return source;
    }

    private static Bitmap RotateBitmap90(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        
        // Читаем исходные пиксели
        var srcPixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* ptr = srcPixels)
            {
                source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, srcPixels.Length, width * 4);
            }
        }
        
        var newBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(height, width),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        
        using var dstBuffer = newBitmap.Lock();
        
        unsafe
        {
            fixed (byte* srcPtr = srcPixels)
            {
                var src = (uint*)srcPtr;
                var dst = (uint*)dstBuffer.Address;
                
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var srcIdx = y * width + x;
                        var dstIdx = x * height + (height - 1 - y);
                        dst[dstIdx] = src[srcIdx];
                    }
                }
            }
        }
        
        return newBitmap;
    }

    private static Bitmap RotateBitmap180(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        
        // Читаем исходные пиксели
        var srcPixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* ptr = srcPixels)
            {
                source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, srcPixels.Length, width * 4);
            }
        }
        
        var newBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(width, height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        
        using var dstBuffer = newBitmap.Lock();
        
        unsafe
        {
            fixed (byte* srcPtr = srcPixels)
            {
                var src = (uint*)srcPtr;
                var dst = (uint*)dstBuffer.Address;
                
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var srcIdx = y * width + x;
                        var dstIdx = (height - 1 - y) * width + (width - 1 - x);
                        dst[dstIdx] = src[srcIdx];
                    }
                }
            }
        }
        
        return newBitmap;
    }

    private static Bitmap RotateBitmap270(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        
        // Читаем исходные пиксели
        var srcPixels = new byte[width * height * 4];
        unsafe
        {
            fixed (byte* ptr = srcPixels)
            {
                source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, srcPixels.Length, width * 4);
            }
        }
        
        var newBitmap = new Avalonia.Media.Imaging.WriteableBitmap(
            new Avalonia.PixelSize(height, width),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Premul);
        
        using var dstBuffer = newBitmap.Lock();
        
        unsafe
        {
            fixed (byte* srcPtr = srcPixels)
            {
                var src = (uint*)srcPtr;
                var dst = (uint*)dstBuffer.Address;
                
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var srcIdx = y * width + x;
                        var dstIdx = (width - 1 - x) * height + y;
                        dst[dstIdx] = src[srcIdx];
                    }
                }
            }
        }
        
        return newBitmap;
    }
}

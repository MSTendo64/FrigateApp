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
    /// <summary>Системный идентификатор камеры (для API, кэша и т.д.).</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>Имя для отображения: friendly_name из конфига, если задан, иначе системное имя.</summary>
    [ObservableProperty]
    private string _displayName = "";

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

    /// <summary>Ширина плитки камеры (для фиксации размера ячейки в сетке).</summary>
    [ObservableProperty]
    private double _tileWidth = 240;

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

    /// <summary>Путь к локальному fMP4-файлу из wss sub-потока; когда задан — плитка показывает видео вместо снимка.</summary>
    [ObservableProperty]
    private string? _videoFilePath;

    private readonly FrigateApiService _api;
    private readonly CameraSnapshotCache? _snapshotCache;
    private WssStreamHandle? _wssHandle;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _isVisible = true;

    private readonly Action? _onOpenPlayer;
    private readonly Action<CameraItemViewModel>? _onZoomChanged;
    private readonly string _subStreamName;
    private readonly WssStreamPoolManager? _wssPool;

    public CameraItemViewModel(
        string cameraName,
        FrigateApiService api,
        Action? onOpenPlayer = null,
        CameraZoomState? initialZoom = null,
        Action<CameraItemViewModel>? onZoomChanged = null,
        bool isZoomEnabled = false,
        CameraSnapshotCache? snapshotCache = null,
        float rotation = 0f,
        double tileScale = 1.0,
        string? friendlyName = null,
        string? subStreamName = null,
        WssStreamPoolManager? wssPool = null)
    {
        Name = cameraName;
        _subStreamName = !string.IsNullOrWhiteSpace(subStreamName) ? subStreamName : $"{cameraName}_sub";
        DisplayName = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName : cameraName;
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onOpenPlayer = onOpenPlayer;
        _onZoomChanged = onZoomChanged;
        _snapshotCache = snapshotCache;
        IsZoomEnabled = isZoomEnabled;
        Rotation = rotation;
        _tileScale = tileScale;
        _wssPool = wssPool;
        
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
        System.Diagnostics.Debug.WriteLine($"[{Name}] Visibility changed: {isVisible}");
    }

    public async void StartRefresh()
    {
        if (_disposed) return;
        StopRefresh();

        VideoFilePath = null;
        StatsText = "WSS • подключение…";

        if (_wssPool != null)
        {
            // Используем пул потоков с ограничением количества одновременных подключений
            _wssHandle = await _wssPool.AcquireStreamAsync(
                Name,
                _subStreamName,
                _api,
                OnWssFileReady,
                OnWssError).ConfigureAwait(false);

            if (_wssHandle == null)
            {
                // Не удалось получить поток (например, отмена)
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                    {
                        HasError = true;
                        ErrorText = "Не удалось получить поток";
                        StatsText = "WSS • ошибка";
                    }
                });
            }
            else
            {
                _wssHandle.Stream.FileReady += OnWssFileReadyDirect;
                _wssHandle.Stream.ErrorOccurred += OnWssErrorDirect;
            }
        }
        else
        {
            // Старое поведение без пула (для совместимости)
            var wssStream = new WssFmp4StreamService(_api);
            wssStream.FileReady += OnWssFileReady;
            wssStream.ErrorOccurred += OnWssError;
            _ = wssStream.StartAsync(_subStreamName);
        }
    }

    private void OnWssFileReadyDirect(string filePath) => OnWssFileReady(filePath);
    private void OnWssErrorDirect(string? message) => OnWssError(message);

    private void OnWssFileReady(string filePath)
    {
        if (_disposed) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            VideoFilePath = filePath;
            HasError = false;
            ErrorText = "";
            StatsText = "WSS";
        });
    }

    private void OnWssError(string? message)
    {
        if (_disposed) return;
        var msg = message ?? "Ошибка потока";
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            HasError = true;
            ErrorText = msg;
            StatsText = "WSS • ошибка";
        });
    }

    public void StopRefresh()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (_wssHandle != null)
        {
            _wssHandle.Stream.FileReady -= OnWssFileReadyDirect;
            _wssHandle.Stream.ErrorOccurred -= OnWssErrorDirect;
            _wssHandle.Dispose();
            _wssHandle = null;
        }

        VideoFilePath = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefresh();
        Snapshot = null;
    }

    /// <summary>Обновить масштаб плитки.</summary>
    public void UpdateTileScale(double scale)
    {
        _tileScale = scale;
        UpdateTileHeight(IsVertical);
    }

    /// <summary>Обновить размер плитки с учетом вертикальности и масштаба.</summary>
    private void UpdateTileHeight(bool isVertical)
    {
        var baseWidth = 240 * _tileScale;
        var baseHeight = 160 * _tileScale;
        TileWidth = baseWidth;
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

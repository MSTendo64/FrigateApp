using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// Лёгкая ViewModel для камеры в сетке.
/// Использует snapshot-обновления (JPEG) вместо видеопотока для экономии ресурсов.
/// Поддерживает 30+ камер одновременно.
/// </summary>
public partial class LightCameraItemViewModel : ViewModelBase, IDisposable
{
    /// <summary>Системный идентификатор камеры.</summary>
    [ObservableProperty]
    private string _name = "";

    /// <summary>Имя для отображения.</summary>
    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private Bitmap? _snapshot;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _isZoomEnabled;

    [ObservableProperty]
    private double _tileWidth = 240;

    [ObservableProperty]
    private double _tileHeight = 160;

    [ObservableProperty]
    private bool _isVertical;

    [ObservableProperty]
    private float _rotation;

    [ObservableProperty]
    private string _statsText = "";

    [ObservableProperty]
    private double _zoomLevel = 1;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    private readonly FrigateApiService _api;
    private readonly Action? _onOpenPlayer;
    private readonly Action<LightCameraItemViewModel>? _onZoomChanged;
    private readonly CameraSnapshotCache? _snapshotCache;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private bool _isVisible = true;
    private readonly int _updateIntervalMs;
    private readonly int _snapshotHeight;
    private readonly object _snapshotLock = new();

    public LightCameraItemViewModel(
        string cameraName,
        FrigateApiService api,
        Action? onOpenPlayer = null,
        Action<LightCameraItemViewModel>? onZoomChanged = null,
        CameraSnapshotCache? snapshotCache = null,
        float rotation = 0f,
        double tileScale = 1.0,
        string? friendlyName = null,
        int updateIntervalMs = 1000,
        int snapshotHeight = 180,
        bool isZoomEnabled = false)
    {
        Name = cameraName;
        DisplayName = !string.IsNullOrWhiteSpace(friendlyName) ? friendlyName : cameraName;
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onOpenPlayer = onOpenPlayer;
        _onZoomChanged = onZoomChanged;
        _snapshotCache = snapshotCache;
        Rotation = rotation;
        _updateIntervalMs = updateIntervalMs;
        _snapshotHeight = snapshotHeight;
        IsZoomEnabled = isZoomEnabled;

        // Загрузить кэш
        if (_snapshotCache != null)
        {
            var cached = _snapshotCache.Get(cameraName);
            if (cached != null)
                Snapshot = cached;

            var cachedIsVertical = _snapshotCache.GetIsVertical(cameraName);
            if (cachedIsVertical.HasValue)
            {
                IsVertical = cachedIsVertical.Value;
                UpdateTileHeight(cachedIsVertical.Value);
            }
        }
        else
        {
            UpdateTileHeight(false);
        }

        TileWidth = 240 * tileScale;
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void OpenPlayer()
    {
        _onOpenPlayer?.Invoke();
    }

    /// <summary>Запустить обновление snapshots.</summary>
    public void StartRefresh()
    {
        if (_disposed) return;
        StopRefresh();
        _cts = new CancellationTokenSource();
        _ = RefreshLoopAsync(_cts.Token);
    }

    private async Task RefreshLoopAsync(CancellationToken ct)
    {
        var consecutiveErrors = 0;
        var maxErrors = 5;

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                if (_isVisible)
                {
                    var bytes = await _api.GetLatestFrameBytesAsync(Name, _snapshotHeight, ct).ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        consecutiveErrors = 0;
                        await UpdateSnapshotAsync(bytes).ConfigureAwait(false);
                        StatsText = $"{bytes.Length / 1024} KB";
                    }
                    else
                    {
                        consecutiveErrors++;
                        if (consecutiveErrors >= maxErrors)
                        {
                            HasError = true;
                            ErrorText = "Нет данных от камеры";
                            StatsText = "Ошибка";
                        }
                    }
                }

                // Ждём следующего обновления
                try
                {
                    await Task.Delay(_updateIntervalMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                System.Diagnostics.Debug.WriteLine($"[{Name}] Snapshot error: {ex.Message}");

                if (consecutiveErrors >= maxErrors)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        HasError = true;
                        ErrorText = ex.Message;
                        StatsText = "Ошибка";
                    });
                }

                try
                {
                    await Task.Delay(Math.Min(_updateIntervalMs * 2, 5000), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task UpdateSnapshotAsync(byte[] bytes)
    {
        if (_disposed) return;

        Bitmap? newBitmap = null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            newBitmap = new Bitmap(ms);

            // Проверка вертикальности
            if (!_snapshotCache?.GetIsVertical(Name).HasValue ?? true)
            {
                var isVert = IsVerticalOrientation(newBitmap.PixelSize.Width, newBitmap.PixelSize.Height, Rotation);
                _snapshotCache?.SetIsVertical(Name, isVert);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsVertical = isVert;
                    UpdateTileHeight(isVert);
                });
            }

            // Сохранить в кэш (кэш сам управляет временем жизни Bitmap)
            _snapshotCache?.Set(Name, newBitmap);

            // Обновить UI
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed) return;
                
                // Блокируем доступ к Snapshot при обновлении
                lock (_snapshotLock)
                {
                    Snapshot = newBitmap;
                }
                
                HasError = false;
                ErrorText = "";
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch
        {
            newBitmap?.Dispose();
            throw;
        }
    }

    public void StopRefresh()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void SetVisibility(bool isVisible)
    {
        _isVisible = isVisible;
    }

    /// <summary>Зум колёсиком относительно курсора (Shift+колесо). При уменьшении до 1 — сброс позиции. Мин = 1, макс = 4.</summary>
    public void ZoomAt(double cursorX, double cursorY, double viewWidth, double viewHeight, double delta)
    {
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopRefresh();
        
        // Блокируем доступ к Snapshot и устанавливаем в null
        lock (_snapshotLock)
        {
            Snapshot = null;
        }
    }

    public void UpdateTileScale(double scale)
    {
        TileWidth = 240 * scale;
        UpdateTileHeight(IsVertical);
    }

    private void UpdateTileHeight(bool isVertical)
    {
        var baseHeight = 160 * (TileWidth / 240);
        TileHeight = isVertical ? (baseHeight * 2 + 4) : baseHeight;
    }

    private static bool IsVerticalOrientation(int width, int height, float rotation)
    {
        var angle = rotation % 360;
        if (angle < 0) angle += 360;
        var isRotated90or270 = Math.Abs(angle - 90) < 1 || Math.Abs(angle - 270) < 1;
        return isRotated90or270 ? width > height : height > width;
    }
}

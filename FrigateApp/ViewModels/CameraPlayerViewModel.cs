using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// ViewModel для полноэкранного проигрывателя камеры.
/// Зум и перетягивание реализованы внутри CameraPlayer контрола.
/// Источник видео — live-поток Frigate по WebSocket (wss, fMP4).
/// </summary>
public partial class CameraPlayerViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private string _cameraName = "";

    /// <summary>Имя для отображения: friendly_name из конфига, если задан, иначе системное имя.</summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>Полный путь к локальному файлу с live-потоком (fMP4), записанным из WebSocket.</summary>
    [ObservableProperty]
    private string? _videoFilePath;

    /// <summary>Снапшот для отображения пока не появилось видео.</summary>
    [ObservableProperty]
    private Bitmap? _snapshot;

    [ObservableProperty]
    private string _statusText = "Загрузка...";

    [ObservableProperty]
    private string? _errorText;

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private float _rotation;

    /// <summary>Зум (1..4). Общий для MJPEG и RTSP.</summary>
    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private double _panX;

    [ObservableProperty]
    private double _panY;

    private readonly FrigateApiService _api;
    private Action? _onBack;
    private WssFmp4StreamService? _wssStream;
    private bool _disposed;
    private bool _triedSubFallback;
    private CancellationTokenSource? _snapshotCts;
    private readonly int _snapshotHeight = 720;

    public CameraPlayerViewModel(string cameraName, FrigateApiService api, Action onBack)
    {
        _cameraName = cameraName ?? "";
        _displayName = _cameraName;
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
    }

    /// <summary>Получить настройки камеры и запустить live-поток по WebSocket.</summary>
    public async Task StartAsync()
    {
        System.Diagnostics.Debug.WriteLine($"StartAsync called for camera: {CameraName}");
        
        IsLoading = true;
        HasError = false;
        ErrorText = null;
        StatusText = "Подключение к камере...";

        try
        {
            System.Diagnostics.Debug.WriteLine("Requesting config from API...");
            var config = await _api.GetConfigAsync().ConfigureAwait(true);
            
            CameraConfig? camConfig = null;
            if (config.Cameras != null && config.Cameras.TryGetValue(CameraName, out camConfig))
            {
                Rotation = camConfig.Rotate;
                DisplayName = !string.IsNullOrWhiteSpace(camConfig.FriendlyName) ? camConfig.FriendlyName : CameraName;
                System.Diagnostics.Debug.WriteLine($"Camera rotation: {Rotation}°, display name: {DisplayName}");
            }
            else
            {
                DisplayName = CameraName;
            }

            var mainStreamName = FrigateApiService.GetStreamNameForLive(camConfig, CameraName, useSubStream: false);
            System.Diagnostics.Debug.WriteLine("Starting WebSocket fMP4 stream...");
            
            // Запускаем обновление снапшотов (15 FPS = 66ms) пока не появится видео
            StartSnapshotRefresh(66);
            
            StartWssStream(mainStreamName);
            StatusText = "Ожидание потока...";
            IsLoading = false;
            System.Diagnostics.Debug.WriteLine("StartAsync completed successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"StartAsync error: {ex}");
            HasError = true;
            ErrorText = $"Ошибка загрузки: {ex.Message}";
            StatusText = "Ошибка";
            IsLoading = false;
        }
    }

    private void StartWssStream(string streamName)
    {
        if (_wssStream != null) return;

        _wssStream = new WssFmp4StreamService(_api);
        _wssStream.FileReady += OnWssFileReady;
        _wssStream.ErrorOccurred += OnWssError;
        _ = _wssStream.StartAsync(streamName);
        System.Diagnostics.Debug.WriteLine($"WebSocket fMP4 stream started: {streamName}");
        
        // Таймаут ожидания потока
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(t =>
        {
            if (_disposed) return;
            if (string.IsNullOrEmpty(VideoFilePath))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed && string.IsNullOrEmpty(VideoFilePath))
                    {
                        StatusText = "Таймаут подключения...";
                        HasError = true;
                        ErrorText = "Не удалось получить поток за 10 сек";
                    }
                });
            }
        });
    }

    /// <summary>Запустить обновление снапшотов с заданным интервалом.</summary>
    private void StartSnapshotRefresh(int intervalMs)
    {
        _snapshotCts?.Cancel();
        _snapshotCts?.Dispose();
        _snapshotCts = new CancellationTokenSource();
        _ = RefreshSnapshotsAsync(_snapshotCts.Token, intervalMs);
    }

    private async Task RefreshSnapshotsAsync(CancellationToken ct, int intervalMs)
    {
        var consecutiveErrors = 0;
        var maxErrors = 10;

        while (!ct.IsCancellationRequested && !_disposed && _snapshotCts != null && !_snapshotCts.IsCancellationRequested)
        {
            try
            {
                // Если видео уже появилось — останавливаем снапшоты
                if (!string.IsNullOrEmpty(VideoFilePath))
                {
                    System.Diagnostics.Debug.WriteLine("Video appeared, stopping snapshots");
                    break;
                }

                var bytes = await _api.GetLatestFrameBytesAsync(CameraName, _snapshotHeight, ct).ConfigureAwait(false);
                if (bytes != null && bytes.Length > 0)
                {
                    consecutiveErrors = 0;
                    await UpdateSnapshotAsync(bytes).ConfigureAwait(false);
                }
                else
                {
                    consecutiveErrors++;
                }

                if (consecutiveErrors >= maxErrors)
                {
                    System.Diagnostics.Debug.WriteLine("Too many snapshot errors, stopping");
                    break;
                }

                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                System.Diagnostics.Debug.WriteLine($"Snapshot error: {ex.Message}");
                if (consecutiveErrors >= maxErrors)
                    break;
                try { await Task.Delay(intervalMs * 2, ct).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    private async Task UpdateSnapshotAsync(byte[] bytes)
    {
        if (_disposed || _snapshotCts == null || _snapshotCts.IsCancellationRequested) return;

        Bitmap? newBitmap = null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            newBitmap = new Bitmap(ms);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || _snapshotCts == null || _snapshotCts.IsCancellationRequested)
                {
                    newBitmap?.Dispose();
                    return;
                }
                // Утилизируем старый снапшот
                var oldSnapshot = Snapshot;
                Snapshot = newBitmap;
                if (oldSnapshot != null && oldSnapshot != newBitmap)
                {
                    try { oldSnapshot.Dispose(); } catch { }
                }
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        catch
        {
            newBitmap?.Dispose();
            throw;
        }
    }

    private void OnWssFileReady(string filePath)
    {
        if (_disposed) return;
        System.Diagnostics.Debug.WriteLine($"WebSocket live file ready: {filePath}");

        // Останавливаем снапшоты — видео появилось
        try
        {
            _snapshotCts?.Cancel();
        }
        catch { }

        // Проверяем размер файла
        var fileInfo = new System.IO.FileInfo(filePath);
        var fileSizeKb = fileInfo.Length / 1024;
        System.Diagnostics.Debug.WriteLine($"File size: {fileSizeKb} KB");

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            VideoFilePath = filePath;
            IsLoading = false;
            StatusText = fileSizeKb > 0
                ? $"Поток подключён ({fileSizeKb} KB)"
                : "Поток подключён (ожидание данных...)";

            // Если файл слишком маленький (< 10 KB), возможно поток ещё не начался
            if (fileSizeKb < 10)
            {
                _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(t =>
                {
                    if (_disposed || !string.IsNullOrEmpty(ErrorText)) return;

                    // Проверяем размер снова
                    var newFileInfo = new System.IO.FileInfo(filePath);
                    var newSizeKb = newFileInfo.Length / 1024;
                    if (newSizeKb < 10)
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (!_disposed && string.IsNullOrEmpty(ErrorText))
                            {
                                StatusText = "Поток есть, но нет данных";
                                HasError = true;
                                ErrorText = "Пустой поток — проверьте камеру";
                            }
                        });
                    }
                });
            }
        });
    }

    private void OnWssError(string? message)
    {
        System.Diagnostics.Debug.WriteLine($"WebSocket fMP4 error: {message}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (!_triedSubFallback)
            {
                _triedSubFallback = true;
                _wssStream?.Stop();
                _wssStream?.Dispose();
                _wssStream = null;
                StatusText = "Повтор (sub-поток)…";
                HasError = false;
                ErrorText = null;
                _ = _api.GetConfigAsync().ContinueWith(t =>
                {
                    if (_disposed || !t.IsCompletedSuccessfully || t.Result?.Cameras == null) return;
                    var camConfig = t.Result.Cameras.TryGetValue(CameraName, out var c) ? c : null;
                    var subStreamName = FrigateApiService.GetStreamNameForLive(camConfig, CameraName, useSubStream: true);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposed) return;
                        StartWssStream(subStreamName);
                    });
                });
                return;
            }
            HasError = true;
            ErrorText = message;
            StatusText = "Ошибка потока";
        });
    }

    /// <summary>Зум колёсиком относительно точки (для MJPEG / растровых кадров). viewW/viewH — размер области, imgW/imgH — размер изображения.</summary>
    public void ZoomAt(double viewW, double viewH, double imgW, double imgH, double cursorX, double cursorY, double wheelDelta)
    {
        if (viewW <= 0 || viewH <= 0 || imgW <= 0 || imgH <= 0) return;
        var factor = wheelDelta > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(ZoomLevel * factor, 1.0, 4.0);
        if (newZoom == ZoomLevel) return;

        var viewAspect = viewW / viewH;
        var imgAspect = imgW / imgH;
        double renderW, renderH, offsetX, offsetY;
        if (viewAspect > imgAspect)
        {
            renderH = viewH;
            renderW = viewH * imgAspect;
            offsetX = (viewW - renderW) / 2;
            offsetY = 0;
        }
        else
        {
            renderW = viewW;
            renderH = viewW / imgAspect;
            offsetX = 0;
            offsetY = (viewH - renderH) / 2;
        }
        var imgX = cursorX - offsetX;
        var imgY = cursorY - offsetY;

        if (newZoom <= 1.0)
        {
            ZoomLevel = 1.0;
            PanX = 0;
            PanY = 0;
            return;
        }
        PanX = imgX - (imgX - PanX) * (newZoom / ZoomLevel);
        PanY = imgY - (imgY - PanY) * (newZoom / ZoomLevel);
        ZoomLevel = newZoom;
        ClampPan(viewW, viewH, renderW, renderH);
    }

    /// <summary>Сдвиг кадра (для MJPEG / растровых кадров). После вызова View должен вызвать ClampPan с актуальными размерами.</summary>
    public void PanBy(double deltaX, double deltaY)
    {
        PanX += deltaX;
        PanY += deltaY;
    }

    /// <summary>Ограничить пан в пределах изображения. Вызывается из View с размерами области и render rect.</summary>
    public void ClampPan(double viewW, double viewH, double renderW, double renderH)
    {
        if (ZoomLevel <= 1.0) { PanX = 0; PanY = 0; return; }
        var scaledW = renderW * ZoomLevel;
        var scaledH = renderH * ZoomLevel;
        var minPanX = Math.Min(0, renderW - scaledW);
        var minPanY = Math.Min(0, renderH - scaledH);
        PanX = Math.Clamp(PanX, minPanX, 0);
        PanY = Math.Clamp(PanY, minPanY, 0);
    }

    /// <summary>Вызывается из View когда плеер начал показывать кадры.</summary>
    public void OnVideoStarted()
    {
        System.Diagnostics.Debug.WriteLine("Live video started showing frames");
        StatusText = "Воспроизведение";
    }

    private void StopWssStream()
    {
        if (_wssStream != null)
        {
            _wssStream.FileReady -= OnWssFileReady;
            _wssStream.ErrorOccurred -= OnWssError;
            _wssStream.Stop();
            _wssStream.Dispose();
            _wssStream = null;
            System.Diagnostics.Debug.WriteLine("WebSocket fMP4 stream stopped");
        }
    }

    [RelayCommand]
    private void Back()
    {
        // Сначала вызываем обратный вызов, потом dispose
        // Это предотвращает использование утилизированного ViewModel
        var onBack = _onBack;
        _onBack = null; // Предотвращаем повторный вызов
        onBack?.Invoke();
        
        // Dispose после возврата
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Останавливаем снапшоты
        _snapshotCts?.Cancel();
        _snapshotCts?.Dispose();
        _snapshotCts = null;

        var filePath = VideoFilePath;
        VideoFilePath = null;

        StopWssStream();

        // Утилизируем снапшот
        Snapshot?.Dispose();
        Snapshot = null;

        // Удаляем временный файл
        if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
        {
            try
            {
                System.IO.File.Delete(filePath);
                System.Diagnostics.Debug.WriteLine($"Deleted temp file: {filePath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to delete temp file {filePath}: {ex.Message}");
            }
        }
    }
}

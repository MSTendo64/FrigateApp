using System;
using System.Buffers;
using System.Runtime.InteropServices;
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
    private readonly Action _onBack;
    private WssFmp4StreamService? _wssStream;
    private bool _disposed;
    private bool _triedSubFallback;

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
    }

    private void OnWssFileReady(string filePath)
    {
        if (_disposed) return;
        System.Diagnostics.Debug.WriteLine($"WebSocket live file ready: {filePath}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            VideoFilePath = filePath;
            IsLoading = false;
            StatusText = "Поток подключён";
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
        Dispose();
        _onBack();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopWssStream();
    }
}

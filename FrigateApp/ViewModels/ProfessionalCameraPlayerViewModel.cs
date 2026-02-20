using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// Профессиональная ViewModel для просмотра камеры.
/// </summary>
public partial class ProfessionalCameraPlayerViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private string _cameraName = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _videoFilePath;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private float _rotation;

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

    public ProfessionalCameraPlayerViewModel(string cameraName, FrigateApiService api, Action onBack)
    {
        _cameraName = cameraName ?? "";
        _displayName = _cameraName;
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
    }

    public async Task StartAsync()
    {
        IsLoading = true;
        HasError = false;
        ErrorText = "";
        StatusText = "Подключение...";

        try
        {
            var config = await _api.GetConfigAsync().ConfigureAwait(true);

            CameraConfig? camConfig = null;
            if (config.Cameras != null && config.Cameras.TryGetValue(CameraName, out camConfig))
            {
                Rotation = camConfig.Rotate;
                DisplayName = !string.IsNullOrWhiteSpace(camConfig.FriendlyName) ? camConfig.FriendlyName : CameraName;
            }

            var mainStreamName = FrigateApiService.GetStreamNameForLive(camConfig, CameraName, useSubStream: false);
            StartWssStream(mainStreamName);
            IsLoading = false;
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = ex.Message;
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
    }

    private void OnWssFileReady(string filePath)
    {
        if (_disposed) return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            VideoFilePath = filePath;
            IsLoading = false;
            StatusText = "Поток подключён";
        });
    }

    private void OnWssError(string? message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (!_triedSubFallback)
            {
                _triedSubFallback = true;
                _wssStream?.Stop();
                _wssStream?.Dispose();
                _wssStream = null;
                StatusText = "Повтор...";
                HasError = false;
                ErrorText = "";
                _ = _api.GetConfigAsync().ContinueWith(t =>
                {
                    if (_disposed || !t.IsCompletedSuccessfully || t.Result?.Cameras == null) return;
                    var camConfig = t.Result.Cameras.TryGetValue(CameraName, out var c) ? c : null;
                    var subStreamName = FrigateApiService.GetStreamNameForLive(camConfig, CameraName, useSubStream: true);
                    Dispatcher.UIThread.Post(() => StartWssStream(subStreamName));
                });
                return;
            }
            HasError = true;
            ErrorText = message ?? "Ошибка";
            StatusText = "Ошибка";
        });
    }

    public void OnVideoStarted()
    {
        StatusText = "Воспроизведение";
    }

    [RelayCommand]
    private void Back()
    {
        var onBack = _onBack;
        Dispose();
        onBack?.Invoke();
    }

    [RelayCommand]
    private void ResetZoom()
    {
        ZoomLevel = 1.0;
        PanX = 0;
        PanY = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wssStream?.Stop();
        _wssStream?.Dispose();

        var filePath = VideoFilePath;
        VideoFilePath = null;
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            try { File.Delete(filePath); } catch { }
        }
    }
}

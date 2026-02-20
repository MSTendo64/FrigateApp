using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// ViewModel для просмотра камеры через HTTP прокси (WSS → HTTP).
/// </summary>
public partial class LowLatencyCameraPlayerViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private string _cameraName = "";

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private string? _videoUrl;

    [ObservableProperty]
    private string _statusText = "Подключение...";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private float _rotation;

    [ObservableProperty]
    private string _latencyText = "";

    [ObservableProperty]
    private bool _isConnected;

    private readonly FrigateApiService _api;
    private readonly Action _onBack;
    private WebSocketToHttpProxy? _proxy;
    private CancellationTokenSource? _latencyCts;
    private bool _disposed;

    public LowLatencyCameraPlayerViewModel(string cameraName, FrigateApiService api, Action onBack)
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

            // Запускаем мониторинг задержки
            _latencyCts = new CancellationTokenSource();
            _ = MonitorLatencyAsync(_latencyCts.Token);

            // Запускаем HTTP прокси для WSS потока
            var wsUrl = _api.GetLiveMseWebSocketUrl(CameraName);
            var cookie = _api.GetCookieHeader();
            _proxy = new WebSocketToHttpProxy(wsUrl, cookie: cookie);
            await _proxy.StartAsync().ConfigureAwait(false);
            
            VideoUrl = _proxy.HttpUrl;
            Console.WriteLine($"[VM] Proxy URL: {VideoUrl}");

            IsConnected = true;
            IsLoading = false;
            StatusText = "Воспроизведение";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorText = ex.Message;
            StatusText = "Ошибка";
            IsLoading = false;
        }
    }

    private void OnStreamError(string message)
    {
        Console.WriteLine($"[VM] Error: {message}");
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            HasError = true;
            ErrorText = message;
            StatusText = "Ошибка потока";
            IsConnected = false;
        });
    }

    private async Task MonitorLatencyAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                if (_proxy?.IsRunning == true)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        LatencyText = "LIVE";
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    [RelayCommand]
    private void Back()
    {
        // Сначала утилизируем ресурсы
        Dispose();
        
        // Потом возвращаемся назад
        var onBack = _onBack;
        onBack?.Invoke();
    }

    [RelayCommand]
    private void Reconnect()
    {
        HasError = false;
        ErrorText = "";
        StatusText = "Переподключение...";
        IsLoading = true;
        VideoUrl = null;
        _ = StartAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Сначала останавливаем прокси
        if (_proxy != null)
        {
            _proxy.Stop();
            _proxy.Dispose();
            _proxy = null;
        }

        // Потом мониторинг
        _latencyCts?.Cancel();
        try { _latencyCts?.Dispose(); } catch { }
        _latencyCts = null;
    }
}

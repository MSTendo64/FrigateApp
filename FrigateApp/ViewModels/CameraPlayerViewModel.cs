using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// ViewModel для полноэкранного проигрывателя RTSP камеры.
/// Зум и перетягивание реализованы внутри CameraPlayer контрола.
/// Временно показывает MJPEG пока RTSP подключается.
/// </summary>
public partial class CameraPlayerViewModel : ViewModelBase, IDisposable
{
    [ObservableProperty]
    private string _cameraName = "";

    [ObservableProperty]
    private string? _rtspUrl;

    [ObservableProperty]
    private Bitmap? _mjpegFrame;

    [ObservableProperty]
    private bool _showMjpeg = true;

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

    /// <summary>Размер отображаемого MJPEG (после поворота) для расчёта зума/пана.</summary>
    [ObservableProperty]
    private double _mjpegDisplayWidth;

    [ObservableProperty]
    private double _mjpegDisplayHeight;

    /// <summary>Показывать RTSP плеер (LibVLC) — когда MJPEG скрыт.</summary>
    public bool ShowRtspPlayer => !ShowMjpeg;

    /// <summary>На Linux использовать RtspClientSharp вместо LibVLC для RTSP.</summary>
    public bool UseRtspSharpPlayer => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    /// <summary>Показывать LibVLC плеер (только не на Linux, когда RTSP активен).</summary>
    public bool ShowLibVlcPlayer => ShowRtspPlayer && !UseRtspSharpPlayer;

    /// <summary>Показывать RtspSharp плеер (только на Linux, когда RTSP активен).</summary>
    public bool ShowRtspSharpPlayer => ShowRtspPlayer && UseRtspSharpPlayer;

    private readonly FrigateApiService _api;
    private readonly Action _onBack;
    private MjpegStreamService? _mjpegStream;
    private bool _disposed;

    public CameraPlayerViewModel(string cameraName, FrigateApiService api, Action onBack)
    {
        _cameraName = cameraName ?? "";
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
    }

    /// <summary>Загрузить RTSP URL и запустить временный MJPEG поток.</summary>
    public async Task StartAsync()
    {
        System.Diagnostics.Debug.WriteLine($"StartAsync called for camera: {CameraName}");
        
        IsLoading = true;
        HasError = false;
        ErrorText = null;
        StatusText = "Подключение...";
        ShowMjpeg = true;

        // Запускаем MJPEG как временный fallback
        StartMjpeg();

        try
        {
            System.Diagnostics.Debug.WriteLine("Requesting config from API...");
            var config = await _api.GetConfigAsync().ConfigureAwait(true);
            
            // Получаем rotation из конфига
            if (config.Cameras != null && config.Cameras.TryGetValue(CameraName, out var camConfig))
            {
                Rotation = camConfig.Rotate;
                System.Diagnostics.Debug.WriteLine($"Camera rotation: {Rotation}°");
            }

            System.Diagnostics.Debug.WriteLine("Requesting RTSP URL from API...");
            var url = await _api.GetCameraRtspUrlAsync(CameraName).ConfigureAwait(true);
            
            System.Diagnostics.Debug.WriteLine($"Received URL: {url}");
            
            if (string.IsNullOrEmpty(url))
            {
                System.Diagnostics.Debug.WriteLine("URL is empty or null");
                HasError = true;
                ErrorText = "RTSP URL не найден в конфигурации камеры";
                StatusText = "Ошибка";
                IsLoading = false;
                return;
            }

            System.Diagnostics.Debug.WriteLine($"Setting RtspUrl property to: {url}");
            RtspUrl = url;
            StatusText = "RTSP подключение...";
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

    private void StartMjpeg()
    {
        if (_mjpegStream != null) return;
        
        _mjpegStream = new MjpegStreamService(_api);
        _mjpegStream.FrameReady += OnMjpegFrameReady;
        _mjpegStream.ErrorOccurred += OnMjpegError;
        _mjpegStream.Start(CameraName);
        System.Diagnostics.Debug.WriteLine("MJPEG stream started");
    }

    private void OnMjpegFrameReady(Bitmap? bitmap)
    {
        if (_disposed || bitmap == null) return;
        
        // Применяем поворот если нужно
        var rotatedBitmap = Rotation != 0 ? RotateBitmap(bitmap, Rotation) : bitmap;
        
        MjpegFrame = rotatedBitmap;
        var ps = rotatedBitmap.PixelSize;
        MjpegDisplayWidth = ps.Width;
        MjpegDisplayHeight = ps.Height;
        
        // Если применили поворот, освобождаем исходный bitmap
        if (rotatedBitmap != bitmap)
            bitmap.Dispose();
    }

    private void OnMjpegError(string? message)
    {
        System.Diagnostics.Debug.WriteLine($"MJPEG error: {message}");
    }

    /// <summary>Зум колёсиком относительно точки (для MJPEG). viewW/viewH — размер области, imgW/imgH — размер изображения.</summary>
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

    /// <summary>Сдвиг кадра (для MJPEG). После вызова View должен вызвать ClampPan с актуальными размерами.</summary>
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

    /// <summary>Вызывается из View когда RTSP начал показывать кадры.</summary>
    public void OnRtspStarted()
    {
        System.Diagnostics.Debug.WriteLine("RTSP started showing frames, stopping MJPEG");
        ShowMjpeg = false;
        StopMjpeg();
        StatusText = "RTSP воспроизведение";
        OnPropertyChanged(nameof(ShowRtspPlayer));
        OnPropertyChanged(nameof(ShowLibVlcPlayer));
        OnPropertyChanged(nameof(ShowRtspSharpPlayer));
    }

    /// <summary>Вызывается из View при таймауте RtspSharp (нет JPEG-кадра — поток скорее всего H.264). Остаёмся на MJPEG.</summary>
    public void OnRtspConnectionTimeout()
    {
        System.Diagnostics.Debug.WriteLine("RTSP connection timeout (no JPEG frame), keeping MJPEG");
        StatusText = "MJPEG (RTSP: поток не в формате JPEG или таймаут)";
    }

    private void StopMjpeg()
    {
        if (_mjpegStream != null)
        {
            _mjpegStream.FrameReady -= OnMjpegFrameReady;
            _mjpegStream.ErrorOccurred -= OnMjpegError;
            _mjpegStream.Stop();
            _mjpegStream = null;
            MjpegFrame = null;
            System.Diagnostics.Debug.WriteLine("MJPEG stream stopped");
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
        StopMjpeg();
    }

    #region Bitmap Rotation

    private static Bitmap RotateBitmap(Bitmap source, float angle)
    {
        var normalized = ((int)angle % 360 + 360) % 360;
        return normalized switch
        {
            90 => RotateBitmap90(source),
            180 => RotateBitmap180(source),
            270 => RotateBitmap270(source),
            _ => source
        };
    }

    private static Bitmap RotateBitmap90(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        var pixelCount = width * height * 4;
        
        var srcPixels = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            unsafe
            {
                fixed (byte* ptr = srcPixels)
                {
                    source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
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
                    var dstPtr = (byte*)dstBuffer.Address;
                    
                    for (var y = 0; y < height; y++)
                    {
                        var srcRowPtr = srcPtr + y * width * 4;
                        for (var x = 0; x < width; x++)
                        {
                            var srcIdx = x * 4;
                            var dstX = height - 1 - y;
                            var dstY = x;
                            var dstIdx = (dstY * height + dstX) * 4;
                            *(uint*)(dstPtr + dstIdx) = *(uint*)(srcRowPtr + srcIdx);
                        }
                    }
                }
            }

            return newBitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    private static Bitmap RotateBitmap180(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        var pixelCount = width * height * 4;
        
        var srcPixels = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            unsafe
            {
                fixed (byte* ptr = srcPixels)
                {
                    source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
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
                    var dstPtr = (byte*)dstBuffer.Address;
                    var totalPixels = width * height;
                    
                    for (var i = 0; i < totalPixels; i++)
                    {
                        var srcIdx = i * 4;
                        var dstIdx = (totalPixels - 1 - i) * 4;
                        *(uint*)(dstPtr + dstIdx) = *(uint*)(srcPtr + srcIdx);
                    }
                }
            }

            return newBitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    private static Bitmap RotateBitmap270(Bitmap source)
    {
        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        var pixelCount = width * height * 4;
        
        var srcPixels = ArrayPool<byte>.Shared.Rent(pixelCount);
        try
        {
            unsafe
            {
                fixed (byte* ptr = srcPixels)
                {
                    source.CopyPixels(new Avalonia.PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
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
                    var dstPtr = (byte*)dstBuffer.Address;
                    for (var y = 0; y < height; y++)
                    {
                        var srcRowPtr = srcPtr + y * width * 4;
                        for (var x = 0; x < width; x++)
                        {
                            var srcIdx = x * 4;
                            var dstX = y;
                            var dstY = width - 1 - x;
                            var dstIdx = (dstY * height + dstX) * 4;
                            *(uint*)(dstPtr + dstIdx) = *(uint*)(srcRowPtr + srcIdx);
                        }
                    }
                }
            }

            return newBitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    #endregion
}

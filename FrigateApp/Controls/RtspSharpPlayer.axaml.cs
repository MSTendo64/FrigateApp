using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RtspClientSharp;
using RtspClientSharp.RawFrames.Video;

namespace FrigateApp.Controls;

public partial class RtspSharpPlayer : UserControl
{
    private double _zoomLevel = 1.0;
    private double _panX;
    private double _panY;
    private int _frameWidth = 1;
    private int _frameHeight = 1;
    private float _rotation;
    private bool _isDragging;
    private Point _dragStart;
    private RtspClient? _client;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _timeoutCts;
    private bool _hasReceivedFirstFrame;

    /// <summary>Таймаут в миллисекундах: если за это время не пришёл ни один JPEG-кадр — считаем подключение неудачным.</summary>
    private const int NoFrameTimeoutMs = 12_000;

    public event Action? FirstFrameReceived;
    public event Action<double, double, double>? ZoomStateChanged;
    /// <summary>Вызывается, если за NoFrameTimeoutMs не пришёл ни один отображаемый кадр (например, поток в H.264, а не JPEG).</summary>
    public event Action? ConnectionTimeout;

    public RtspSharpPlayer()
    {
        InitializeComponent();
        ClipToBounds = true;
        Focusable = true;
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    public void Play(string rtspUrl, float rotation = 0f)
    {
        Stop();
        _rotation = rotation;
        _hasReceivedFirstFrame = false;

        if (string.IsNullOrWhiteSpace(rtspUrl) || !Uri.TryCreate(rtspUrl, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine("[RtspSharpPlayer] Invalid RTSP URL");
            return;
        }

        NetworkCredential credentials;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(new[] { ':' }, 2, StringSplitOptions.None);
            var user = parts.Length > 0 ? Uri.UnescapeDataString(parts[0]) : "";
            var pass = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            credentials = new NetworkCredential(user, pass);
        }
        else
        {
            credentials = new NetworkCredential("", "");
        }

        var connectionParameters = new ConnectionParameters(uri, credentials);
        connectionParameters.RtpTransport = RtpTransportProtocol.TCP;

        _client = new RtspClient(connectionParameters);
        _client.FrameReceived += OnFrameReceived;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Таймаут: если за NoFrameTimeoutMs не пришёл ни один JPEG — поток скорее всего H.264, отображать нечем
        _timeoutCts = new CancellationTokenSource();
        _ = RunNoFrameTimeoutAsync(_timeoutCts.Token);

        _ = Task.Run(async () =>
        {
            try
            {
                await _client.ConnectAsync(token).ConfigureAwait(false);
                await _client.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RtspSharpPlayer] Receive error: {ex.Message}");
            }
        }, token);
    }

    private async Task RunNoFrameTimeoutAsync(CancellationToken timeoutToken)
    {
        try
        {
            await Task.Delay(NoFrameTimeoutMs, timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_hasReceivedFirstFrame) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!_hasReceivedFirstFrame)
            {
                System.Diagnostics.Debug.WriteLine("[RtspSharpPlayer] No frame received within timeout (stream may be H.264, not JPEG)");
                ConnectionTimeout?.Invoke();
                Stop();
            }
        });
    }

    public void Stop()
    {
        _timeoutCts?.Cancel();
        _timeoutCts?.Dispose();
        _timeoutCts = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_client != null)
        {
            _client.FrameReceived -= OnFrameReceived;
            _client.Dispose();
            _client = null;
        }
        FrameImage.Source = null;
    }

    public void SetZoomState(double zoomLevel, double panX, double panY)
    {
        _zoomLevel = Math.Clamp(zoomLevel, 1.0, 4.0);
        _panX = panX;
        _panY = panY;
        UpdateTransform();
    }

    private void OnFrameReceived(object sender, RtspClientSharp.RawFrames.RawFrame frame)
    {
        if (frame is not RawJpegFrame jpegFrame) return;

        var segment = jpegFrame.FrameSegment;
        if (segment.Array == null || segment.Count <= 0) return;

        var bytes = new byte[segment.Count];
        Array.Copy(segment.Array, segment.Offset, bytes, 0, segment.Count);

        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            var w = bitmap.PixelSize.Width;
            var h = bitmap.PixelSize.Height;

            if (_rotation != 0)
            {
                var rotated = RotateBitmap(bitmap, _rotation);
                bitmap.Dispose();
                bitmap = rotated;
                var rw = bitmap.PixelSize.Width;
                var rh = bitmap.PixelSize.Height;
                _frameWidth = rw;
                _frameHeight = rh;
            }
            else
            {
                _frameWidth = w;
                _frameHeight = h;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var old = FrameImage.Source as Bitmap;
                FrameImage.Source = bitmap;
                old?.Dispose();
                UpdateTransform();

                if (!_hasReceivedFirstFrame)
                {
                    _hasReceivedFirstFrame = true;
                    FirstFrameReceived?.Invoke();
                }
            }, Avalonia.Threading.DispatcherPriority.Render);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RtspSharpPlayer] Decode error: {ex.Message}");
        }
    }

    private static Bitmap RotateBitmap(Bitmap source, float angle)
    {
        var normalized = ((int)angle % 360 + 360) % 360;
        if (normalized == 0) return source;
        if (normalized == 90) return Rotate90(source);
        if (normalized == 180) return Rotate180(source);
        if (normalized == 270) return Rotate270(source);
        return source;
    }

    private static Bitmap Rotate90(Bitmap source)
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
                    source.CopyPixels(new PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
                }
            }
            var dst = new WriteableBitmap(new PixelSize(height, width), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var dstLock = dst.Lock())
            {
                unsafe
                {
                    var dstPtr = (byte*)dstLock.Address;
                    fixed (byte* srcPtr = srcPixels)
                    {
                        for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            var srcIdx = (y * width + x) * 4;
                            var dstX = height - 1 - y;
                            var dstY = x;
                            var dstIdx = (dstY * height + dstX) * 4;
                            *(uint*)(dstPtr + dstIdx) = *(uint*)(srcPtr + srcIdx);
                        }
                    }
                }
            }
            return dst;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    private static Bitmap Rotate180(Bitmap source)
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
                    source.CopyPixels(new PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
                }
            }
            var dst = new WriteableBitmap(source.PixelSize, new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var dstLock = dst.Lock())
            {
                unsafe
                {
                    var dstPtr = (byte*)dstLock.Address;
                    var n = width * height;
                    fixed (byte* srcPtr = srcPixels)
                    {
                        for (var i = 0; i < n; i++)
                            *(uint*)(dstPtr + (n - 1 - i) * 4) = *(uint*)(srcPtr + i * 4);
                    }
                }
            }
            return dst;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    private static Bitmap Rotate270(Bitmap source)
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
                    source.CopyPixels(new PixelRect(0, 0, width, height), (nint)ptr, pixelCount, width * 4);
                }
            }
            var dst = new WriteableBitmap(new PixelSize(height, width), new Vector(96, 96), Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var dstLock = dst.Lock())
            {
                unsafe
                {
                    var dstPtr = (byte*)dstLock.Address;
                    fixed (byte* srcPtr = srcPixels)
                    {
                        for (var y = 0; y < height; y++)
                        for (var x = 0; x < width; x++)
                        {
                            var srcIdx = (y * width + x) * 4;
                            var dstX = y;
                            var dstY = width - 1 - x;
                            var dstIdx = (dstY * height + dstX) * 4;
                            *(uint*)(dstPtr + dstIdx) = *(uint*)(srcPtr + srcIdx);
                        }
                    }
                }
            }
            return dst;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(srcPixels);
        }
    }

    private void UpdateTransform()
    {
        var group = RootPanel?.RenderTransform as TransformGroup;
        var scale = group?.Children?.Count > 0 ? group.Children[0] as ScaleTransform : null;
        var translate = group?.Children?.Count > 1 ? group.Children[1] as TranslateTransform : null;
        if (scale != null)
        {
            scale.ScaleX = _zoomLevel;
            scale.ScaleY = _zoomLevel;
        }
        if (translate != null)
        {
            translate.X = _panX;
            translate.Y = _panY;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(_zoomLevel * factor, 1.0, 4.0);
        if (Math.Abs(newZoom - _zoomLevel) < 1e-6) { e.Handled = true; return; }

        var (renderW, renderH, offsetX, offsetY) = GetRenderSize();
        var imgX = pos.X - offsetX;
        var imgY = pos.Y - offsetY;
        _panX = imgX - (imgX - _panX) * (newZoom / _zoomLevel);
        _panY = imgY - (imgY - _panY) * (newZoom / _zoomLevel);
        _zoomLevel = newZoom;
        ClampPan();
        UpdateTransform();
        ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _dragStart = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            var pos = e.GetPosition(this);
            _panX += pos.X - _dragStart.X;
            _panY += pos.Y - _dragStart.Y;
            _dragStart = pos;
            ClampPan();
            UpdateTransform();
            ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private (double renderW, double renderH, double offsetX, double offsetY) GetRenderSize()
    {
        var viewW = Bounds.Width;
        var viewH = Bounds.Height;
        if (viewW <= 0 || viewH <= 0 || _frameWidth <= 0 || _frameHeight <= 0)
            return (viewW, viewH, 0, 0);
        var viewAspect = viewW / viewH;
        var imgAspect = (double)_frameWidth / _frameHeight;
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
        return (renderW, renderH, offsetX, offsetY);
    }

    private void ClampPan()
    {
        if (_zoomLevel <= 1.0) { _panX = 0; _panY = 0; return; }
        var (renderW, renderH, _, _) = GetRenderSize();
        if (renderW <= 0 || renderH <= 0) return;
        var scaledW = renderW * _zoomLevel;
        var scaledH = renderH * _zoomLevel;
        _panX = Math.Clamp(_panX, Math.Min(0, renderW - scaledW), 0);
        _panY = Math.Clamp(_panY, Math.Min(0, renderH - scaledH), 0);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Stop();
    }
}

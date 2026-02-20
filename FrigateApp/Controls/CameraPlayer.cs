using System;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using LibVLCSharp.Shared;
using SkiaSharp;

namespace FrigateApp.Controls;

/// <summary>
/// Кастомный контрол для воспроизведения RTSP потока с поддержкой зума и перетягивания.
/// Использует LibVLC для декодирования и Skia для отрисовки.
/// </summary>
public class CameraPlayer : Control
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private byte[]? _frameBuffer;
    private int _videoWidth;
    private int _videoHeight;
    private int _realVideoWidth;
    private int _realVideoHeight;
    private bool _realSizeDetected;
    private readonly object _frameLock = new();
    private bool _hasReceivedFirstFrame;
    
    // Для работы с растущими файлами
    private string? _currentFilePath;
    private long _lastFileSize;
    private System.Timers.Timer? _fileWatchTimer;
    
    // Зум и пан
    private double _zoomLevel = 1.0;
    private double _panX = 0.0;
    private double _panY = 0.0;
    
    // Перетягивание
    private bool _isDragging;
    private Point _dragStart;

    // Поворот камеры
    private float _rotation = 0f;

    /// <summary>Растянуть видео на всю ячейку (UniformToFill), иначе вписать с сохранением пропорций (Uniform). Для плиток — true.</summary>
    public static readonly StyledProperty<bool> StretchToFillProperty =
        AvaloniaProperty.Register<CameraPlayer, bool>(nameof(StretchToFill), false);

    public bool StretchToFill
    {
        get => GetValue(StretchToFillProperty);
        set => SetValue(StretchToFillProperty, value);
    }

    /// <summary>Событие: получен первый кадр от RTSP потока.</summary>
    public event Action? FirstFrameReceived;

    /// <summary>Событие: зум/пан изменён пользователем (для синхронизации с ViewModel).</summary>
    public event Action<double, double, double>? ZoomStateChanged;

    public CameraPlayer()
    {
        ClipToBounds = true;
        Focusable = true;
        
        PointerWheelChanged += OnPointerWheelChanged;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
    }

    /// <summary>Запустить воспроизведение RTSP потока.</summary>
    public void Play(string rtspUrl, float rotation = 0f)
    {
        Stop();
        _rotation = rotation;
        
        try
        {
            // Инициализация LibVLC (если еще не создан)
            // Опции для правильного отображения вертикальных камер
            _libVlc ??= new LibVLC(enableDebugLogs: false,
                "--no-audio",
                "--network-caching=300",
                "--no-video-title-show");
            
            _mediaPlayer = new MediaPlayer(_libVlc);
            
            // Используем фиксированный буфер, но отслеживаем реальный размер для правильного отображения
            _videoWidth = 1920;
            _videoHeight = 1080;
            _realVideoWidth = 1920;
            _realVideoHeight = 1080;
            _frameBuffer = new byte[_videoWidth * _videoHeight * 4];
            
            // Настройка формата видео
            _mediaPlayer.SetVideoFormat("RV32", (uint)_videoWidth, (uint)_videoHeight, (uint)(_videoWidth * 4));
            _mediaPlayer.SetVideoCallbacks(Lock, null, Display);
            
            // Подписываемся на событие для получения реального размера
            _mediaPlayer.Vout += (sender, args) =>
            {
                System.Diagnostics.Debug.WriteLine($"[Vout event] Count: {args.Count}");
                UpdateRealVideoSize();
            };
            
            // Также пробуем получить размер при каждом кадре пока не определим
            _mediaPlayer.TimeChanged += (sender, args) =>
            {
                if (!_realSizeDetected)
                {
                    UpdateRealVideoSize();
                }
            };
            
            // Локальный файл (растущий fMP4 от wss) — FromPath и опции для live
            Media media;
            var path = rtspUrl?.Trim() ?? "";
            bool isLocalFile = path.Length > 0 && (Path.IsPathRooted(path) || path.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
            if (isLocalFile)
            {
                var filePath = path.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    ? new Uri(path).LocalPath
                    : path;
                
                // Используем file:// URL для правильного чтения растущего файла
                var fileUrl = $"file:///{filePath.Replace("\\", "/")}";
                media = new Media(_libVlc, fileUrl, FromType.FromLocation);
                
                // Опции для live-файлов (растущий файл)
                media.AddOption(":input-repeat=0");
                media.AddOption(":no-video-title-show");
                media.AddOption(":no-audio");
                media.AddOption(":live-caching=50");
                media.AddOption(":file-caching=50");
                media.AddOption(":demux=mp4");
                media.AddOption(":no-demux-file");
            }
            else
            {
                media = new Media(_libVlc, rtspUrl, FromType.FromLocation);
                if (path.StartsWith("rtsp:", StringComparison.OrdinalIgnoreCase))
                    media.AddOption(":rtsp-tcp");
            }
            _mediaPlayer.Play(media);

            System.Diagnostics.Debug.WriteLine($"Playback started for: {path}");

            media.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CameraPlayer Play error: {ex.Message}");
        }
    }

    /// <summary>Воспроизведение по URL (HTTP/HTTPS клип или HLS, RTSP).</summary>
    public void PlayMediaUrl(string url, float rotation = 0f)
    {
        Play(url, rotation);
        
        // Запускаем мониторинг растущего файла
        if (!string.IsNullOrEmpty(url) && (Path.IsPathRooted(url) || url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)))
        {
            StartFileWatch(url);
        }
    }

    /// <summary>Запустить мониторинг растущего файла и перезапуск при изменении.</summary>
    private void StartFileWatch(string url)
    {
        StopFileWatch();
        
        var filePath = url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? new Uri(url).LocalPath
            : url;
        
        _currentFilePath = filePath;
        _lastFileSize = 0;
        
        _fileWatchTimer = new System.Timers.Timer(500); // Проверка каждые 500ms
        _fileWatchTimer.Elapsed += async (s, e) => await CheckFileGrowthAsync();
        _fileWatchTimer.Start();
    }

    private async Task CheckFileGrowthAsync()
    {
        if (string.IsNullOrEmpty(_currentFilePath)) return;
        
        try
        {
            var fileInfo = new FileInfo(_currentFilePath);
            if (!fileInfo.Exists) return;
            
            var newSize = fileInfo.Length;
            var hasGrown = newSize > _lastFileSize + 1024; // Вырос более чем на 1KB
            
            if (hasGrown && _mediaPlayer != null)
            {
                // Файл растёт - проверяем что видео воспроизводится
                var isPlaying = _mediaPlayer.IsPlaying;
                var position = _mediaPlayer.Position;
                
                // Если позиция не меняется или видео не играет - перезапускаем
                if (!isPlaying || position > 0.95)
                {
                    System.Diagnostics.Debug.WriteLine($"[FileWatch] Restarting playback (pos={position:F2}, playing={isPlaying})");
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        _mediaPlayer?.Stop();
                        _mediaPlayer?.Play();
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            }
            
            _lastFileSize = newSize;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FileWatch] Error: {ex.Message}");
        }
    }

    private void StopFileWatch()
    {
        _fileWatchTimer?.Stop();
        _fileWatchTimer?.Dispose();
        _fileWatchTimer = null;
        _currentFilePath = null;
    }

    /// <summary>Остановить воспроизведение.</summary>
    public void Stop()
    {
        StopFileWatch();
        
        try
        {
            _mediaPlayer?.Stop();
            _mediaPlayer?.Dispose();
        }
        catch { }
        _mediaPlayer = null;

        lock (_frameLock)
        {
            _frameBuffer = null;
            _videoWidth = 0;
            _videoHeight = 0;
            _realVideoWidth = 0;
            _realVideoHeight = 0;
            _realSizeDetected = false;
        }

        _hasReceivedFirstFrame = false;
        
        try
        {
            InvalidateVisual();
        }
        catch { }
    }

    /// <summary>Приостановить воспроизведение.</summary>
    public void Pause()
    {
        _mediaPlayer?.Pause();
        RaiseIsPlayingChanged();
    }

    /// <summary>Продолжить воспроизведение после паузы.</summary>
    public void Resume()
    {
        _mediaPlayer?.Play();
        RaiseIsPlayingChanged();
    }

    /// <summary>Переключить паузу/воспроизведение.</summary>
    public void TogglePause()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
        RaiseIsPlayingChanged();
    }

    /// <summary>Текущее состояние: идёт воспроизведение.</summary>
    public bool IsPlaying => _mediaPlayer?.IsPlaying ?? false;

    /// <summary>Событие: изменилось состояние воспроизведения (пауза/play).</summary>
    public event Action? IsPlayingChanged;

    private void RaiseIsPlayingChanged() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsPlayingChanged?.Invoke());

    /// <summary>Перемотать относительно текущей позиции на заданное число секунд.</summary>
    public void SeekRelative(int seconds)
    {
        if (_mediaPlayer == null) return;
        var len = _mediaPlayer.Length;
        var t = _mediaPlayer.Time + seconds * 1000L;
        if (len > 0)
            t = Math.Clamp(t, 0, len);
        else if (t < 0)
            t = 0;
        _mediaPlayer.Time = t;
    }

    /// <summary>Текущая позиция в миллисекундах.</summary>
    public long GetTimeMs() => _mediaPlayer?.Time ?? 0;

    /// <summary>Установить позицию воспроизведения (миллисекунды).</summary>
    public void SetTimeMs(long positionMs)
    {
        if (_mediaPlayer == null) return;
        var len = _mediaPlayer.Length;
        var t = len > 0 ? Math.Clamp(positionMs, 0, len) : Math.Max(0, positionMs);
        _mediaPlayer.Time = t;
    }

    /// <summary>Установить скорость воспроизведения (например 0.5f, 1f, 1.5f, 2f).</summary>
    public void SetRate(float rate)
    {
        _mediaPlayer?.SetRate(rate);
    }

    /// <summary>Установить зум и пан (например из ViewModel при переключении MJPEG→RTSP).</summary>
    public void SetZoomState(double zoomLevel, double panX, double panY)
    {
        _zoomLevel = Math.Clamp(zoomLevel, 1.0, 4.0);
        _panX = panX;
        _panY = panY;
        ClampPan();
        InvalidateVisual();
    }

    /// <summary>Сброс зума и позиции.</summary>
    public void ResetView()
    {
        _zoomLevel = 1.0;
        _panX = 0.0;
        _panY = 0.0;
        ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
        InvalidateVisual();
    }

    /// <summary>Обновляет реальный размер видео из MediaPlayer.</summary>
    private void UpdateRealVideoSize()
    {
        if (_realSizeDetected || _mediaPlayer?.Media == null) return;
        
        try
        {
            var tracks = _mediaPlayer.Media.Tracks;
            if (tracks != null)
            {
                foreach (var track in tracks)
                {
                    if (track.Data.Video.Width > 0 && track.Data.Video.Height > 0)
                    {
                        var newWidth = (int)track.Data.Video.Width;
                        var newHeight = (int)track.Data.Video.Height;
                        
                        // Проверяем что размер изменился
                        if (newWidth != _realVideoWidth || newHeight != _realVideoHeight)
                        {
                            _realVideoWidth = newWidth;
                            _realVideoHeight = newHeight;
                            _realSizeDetected = true;
                            
                            System.Diagnostics.Debug.WriteLine($"[UpdateRealVideoSize] Detected: {_realVideoWidth}x{_realVideoHeight}");
                            Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UpdateRealVideoSize] Error: {ex.Message}");
        }
    }

    /// <summary>Адаптирует размер буфера под реальное соотношение сторон видео.</summary>
    private void AdaptBufferSize(int realWidth, int realHeight)
    {
        lock (_frameLock)
        {
            // Вычисляем оптимальный размер буфера (макс. 1920px по любой стороне)
            const int maxDimension = 1920;
            int bufferWidth, bufferHeight;
            
            if (realWidth > realHeight)
            {
                // Горизонтальное видео: ограничиваем по ширине
                if (realWidth > maxDimension)
                {
                    var scale = (double)maxDimension / realWidth;
                    bufferWidth = maxDimension;
                    bufferHeight = (int)(realHeight * scale);
                }
                else
                {
                    bufferWidth = realWidth;
                    bufferHeight = realHeight;
                }
            }
            else
            {
                // Вертикальное или квадратное: ограничиваем по высоте
                if (realHeight > maxDimension)
                {
                    var scale = (double)maxDimension / realHeight;
                    bufferHeight = maxDimension;
                    bufferWidth = (int)(realWidth * scale);
                }
                else
                {
                    bufferWidth = realWidth;
                    bufferHeight = realHeight;
                }
            }
            
            // Проверяем нужно ли менять размер буфера
            if (bufferWidth == _videoWidth && bufferHeight == _videoHeight)
            {
                // Только обновляем реальный размер
                _realVideoWidth = realWidth;
                _realVideoHeight = realHeight;
                System.Diagnostics.Debug.WriteLine($"Buffer size unchanged: {bufferWidth}x{bufferHeight}");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"Adapting buffer: {_videoWidth}x{_videoHeight} → {bufferWidth}x{bufferHeight} (real: {realWidth}x{realHeight})");
            
            _videoWidth = bufferWidth;
            _videoHeight = bufferHeight;
            _realVideoWidth = realWidth;
            _realVideoHeight = realHeight;
            
            // Пересоздаём буфер
            _frameBuffer = new byte[_videoWidth * _videoHeight * 4];
            
            // Останавливаем текущее воспроизведение
            if (_mediaPlayer != null)
            {
                var wasPlaying = _mediaPlayer.IsPlaying;
                var media = _mediaPlayer.Media;
                
                _mediaPlayer.Stop();
                
                // Обновляем формат видео
                _mediaPlayer.SetVideoFormat("RV32", (uint)_videoWidth, (uint)_videoHeight, (uint)(_videoWidth * 4));
                
                // Перезапускаем
                if (wasPlaying && media != null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _mediaPlayer?.Play(media);
                        System.Diagnostics.Debug.WriteLine("Restarted playback with new buffer size");
                    });
                }
            }
        }
        
        Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);
    }

    // LibVLC callbacks
    private IntPtr Lock(IntPtr opaque, IntPtr planes)
    {
        lock (_frameLock)
        {
            if (_frameBuffer != null)
            {
                unsafe
                {
                    fixed (byte* ptr = _frameBuffer)
                    {
                        System.Runtime.InteropServices.Marshal.WriteIntPtr(planes, (IntPtr)ptr);
                    }
                }
            }
        }
        return IntPtr.Zero;
    }

    private void Display(IntPtr opaque, IntPtr picture)
    {
        // Запросить перерисовку на UI потоке
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Проверяем что не утилизированы
            if (_mediaPlayer == null || _libVlc == null) return;
            
            InvalidateVisual();

            // Уведомить о первом кадре
            if (!_hasReceivedFirstFrame)
            {
                _hasReceivedFirstFrame = true;
                FirstFrameReceived?.Invoke();
                System.Diagnostics.Debug.WriteLine("First RTSP frame received");
            }
        }, Avalonia.Threading.DispatcherPriority.Render);
    }

    // Отрисовка через Skia
    public override void Render(DrawingContext context)
    {
        base.Render(context);

            lock (_frameLock)
            {
                if (_frameBuffer == null || _videoWidth <= 0 || _videoHeight <= 0)
                {
                    // Рисуем черный фон если нет видео
                    context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
                    return;
                }

                var drawOp = new VideoDrawOperation(
                    new Rect(Bounds.Size),
                    _frameBuffer,
                    _videoWidth,
                    _videoHeight,
                    _realVideoWidth,
                    _realVideoHeight,
                    _zoomLevel,
                    _panX,
                    _panY,
                    _rotation,
                    StretchToFill);
                
                context.Custom(drawOp);
            }
    }

    // Зум колёсиком относительно курсора
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var pos = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.1 : 1.0 / 1.1;
        var newZoom = Math.Clamp(_zoomLevel * factor, 1.0, 4.0);

        if (newZoom != _zoomLevel)
        {
            // Вычисляем размер и offset изображения (как в Render)
            var viewW = Bounds.Width;
            var viewH = Bounds.Height;
            if (viewW <= 0 || viewH <= 0)
            {
                _zoomLevel = newZoom;
                InvalidateVisual();
                ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
                e.Handled = true;
                return;
            }

            // Эффективные размеры с учетом поворота
            var normalizedAngle = _rotation % 360;
            if (normalizedAngle < 0) normalizedAngle += 360;
            var isRotated90or270 = Math.Abs(normalizedAngle - 90) < 1 || Math.Abs(normalizedAngle - 270) < 1;
            var effectiveWidth = isRotated90or270 ? (_realVideoWidth > 0 ? _realVideoWidth : _videoWidth) : (_realVideoHeight > 0 ? _realVideoHeight : _videoHeight);
            var effectiveHeight = isRotated90or270 ? (_realVideoHeight > 0 ? _realVideoHeight : _videoHeight) : (_realVideoWidth > 0 ? _realVideoWidth : _videoWidth);

            var viewAspect = viewW / viewH;
            var imgAspect = (double)effectiveWidth / effectiveHeight;

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

            // Позиция курсора относительно изображения (без offset)
            var imgX = pos.X - offsetX;
            var imgY = pos.Y - offsetY;

            // Зумим относительно точки под курсором
            _panX = imgX - (imgX - _panX) * (newZoom / _zoomLevel);
            _panY = imgY - (imgY - _panY) * (newZoom / _zoomLevel);
            _zoomLevel = newZoom;

            ClampPan();
            ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    // Начало перетягивания
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

    // Перетягивание
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging)
        {
            var pos = e.GetPosition(this);
            var deltaX = pos.X - _dragStart.X;
            var deltaY = pos.Y - _dragStart.Y;
            
            _panX += deltaX;
            _panY += deltaY;
            _dragStart = pos;
            
            ClampPan();
            ZoomStateChanged?.Invoke(_zoomLevel, _panX, _panY);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    // Конец перетягивания
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // Ограничение пана в пределах изображения
    private void ClampPan()
    {
        if (_zoomLevel <= 1.0)
        {
            _panX = 0;
            _panY = 0;
            return;
        }

        var viewW = Bounds.Width;
        var viewH = Bounds.Height;
        
        if (viewW <= 0 || viewH <= 0 || _realVideoWidth <= 0 || _realVideoHeight <= 0)
            return;

        // Эффективные размеры с учетом поворота
        var normalizedAngle = _rotation % 360;
        if (normalizedAngle < 0) normalizedAngle += 360;
        var isRotated90or270 = Math.Abs(normalizedAngle - 90) < 1 || Math.Abs(normalizedAngle - 270) < 1;
        var effectiveWidth = isRotated90or270 ? _realVideoHeight : _realVideoWidth;
        var effectiveHeight = isRotated90or270 ? _realVideoWidth : _realVideoHeight;

        // Вычисляем размер отрендеренного изображения (Stretch="Uniform")
        var viewAspect = viewW / viewH;
        var imgAspect = (double)effectiveWidth / effectiveHeight;
        
        double renderW, renderH;
        if (viewAspect > imgAspect)
        {
            renderH = viewH;
            renderW = viewH * imgAspect;
        }
        else
        {
            renderW = viewW;
            renderH = viewW / imgAspect;
        }

        // Размер после зума (масштабируем от 0,0)
        var scaledW = renderW * _zoomLevel;
        var scaledH = renderH * _zoomLevel;

        // Границы пана: можем сдвигать влево/вверх, но не вправо/вниз (т.к. масштабируем от 0,0)
        // Минимальный pan = сдвинуть так, чтобы правый/нижний край был на границе view
        // Максимальный pan = 0 (левый/верхний угол на месте)
        var minPanX = Math.Min(0, renderW - scaledW);
        var minPanY = Math.Min(0, renderH - scaledH);

        _panX = Math.Clamp(_panX, minPanX, 0);
        _panY = Math.Clamp(_panY, minPanY, 0);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        StopFileWatch();
        
        try { Stop(); } catch { }
        
        try
        {
            _libVlc?.Dispose();
        }
        catch { }
        _libVlc = null;
    }

    private class VideoDrawOperation : ICustomDrawOperation
    {
        private readonly Rect _bounds;
        private readonly byte[] _frameBuffer;
        private readonly int _videoWidth;
        private readonly int _videoHeight;
        private readonly int _realVideoWidth;
        private readonly int _realVideoHeight;
        private readonly double _zoomLevel;
        private readonly double _panX;
        private readonly double _panY;
        private readonly float _rotation;
        private readonly bool _stretchToFill;

        public VideoDrawOperation(Rect bounds, byte[] frameBuffer, int videoWidth, int videoHeight,
            int realVideoWidth, int realVideoHeight, double zoomLevel, double panX, double panY, float rotation,
            bool stretchToFill = false)
        {
            _bounds = bounds;
            _frameBuffer = frameBuffer;
            _videoWidth = videoWidth;
            _videoHeight = videoHeight;
            _realVideoWidth = realVideoWidth;
            _realVideoHeight = realVideoHeight;
            _zoomLevel = zoomLevel;
            _panX = panX;
            _panY = panY;
            _rotation = rotation;
            _stretchToFill = stretchToFill;
        }

        public void Dispose() { }

        public Rect Bounds => _bounds;

        public bool HitTest(Point p) => _bounds.Contains(p);

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature == null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            canvas.Save();
            if (_stretchToFill)
                canvas.ClipRect(new SKRect(0, 0, (float)_bounds.Width, (float)_bounds.Height));
            canvas.Clear(SKColors.Black);

            try
            {
                // Создаем SKBitmap из buffer
                var imageInfo = new SKImageInfo(_videoWidth, _videoHeight, SKColorType.Bgra8888);
                
                unsafe
                {
                    fixed (byte* ptr = _frameBuffer)
                    {
                        using var bitmap = new SKBitmap();
                        if (bitmap.InstallPixels(imageInfo, (IntPtr)ptr, imageInfo.RowBytes))
                        {
                            var viewW = (float)_bounds.Width;
                            var viewH = (float)_bounds.Height;
                            
                            // Нормализуем угол поворота
                            var normalizedAngle = _rotation % 360;
                            if (normalizedAngle < 0) normalizedAngle += 360;
                            
                            // При повороте 90° или 270° меняются местами размеры для вычисления aspect ratio
                            var isRotated90or270 = Math.Abs(normalizedAngle - 90) < 1 || Math.Abs(normalizedAngle - 270) < 1;
                            var effectiveWidth = isRotated90or270 ? _realVideoHeight : _realVideoWidth;
                            var effectiveHeight = isRotated90or270 ? _realVideoWidth : _realVideoHeight;
                            
                            // Uniform = вписать в ячейку (letterbox); UniformToFill = заполнить ячейку (обрезка)
                            var viewAspect = viewW / viewH;
                            var imgAspect = (float)effectiveWidth / effectiveHeight;

                            float renderW, renderH, offsetX, offsetY;
                            if (_stretchToFill)
                            {
                                if (viewAspect > imgAspect)
                                {
                                    renderW = viewW;
                                    renderH = viewW / imgAspect;
                                    offsetX = 0;
                                    offsetY = (viewH - renderH) / 2;
                                }
                                else
                                {
                                    renderH = viewH;
                                    renderW = viewH * imgAspect;
                                    offsetX = (viewW - renderW) / 2;
                                    offsetY = 0;
                                }
                            }
                            else
                            {
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
                            }

                            // Применяем offset (центрирование)
                            canvas.Translate(offsetX, offsetY);
                            
                            // Применяем Pan и Zoom ПЕРЕД поворотом (в экранных координатах)
                            canvas.Translate((float)_panX, (float)_panY);
                            canvas.Scale((float)_zoomLevel, (float)_zoomLevel, 0, 0);
                            
                            // Поворачиваем canvas вокруг центра итогового прямоугольника
                            if (_rotation != 0)
                            {
                                canvas.Translate(renderW / 2, renderH / 2);
                                canvas.RotateDegrees(_rotation);
                                // После поворота, позиционируем bitmap так, чтобы его центр был в (0,0)
                                // Размер bitmap = оригинальный (до поворота aspect ratio расчетов)
                                var bmpW = isRotated90or270 ? renderH : renderW;
                                var bmpH = isRotated90or270 ? renderW : renderH;
                                canvas.Translate(-bmpW / 2, -bmpH / 2);
                                
                                var destRect = new SKRect(0, 0, bmpW, bmpH);
                                canvas.DrawBitmap(bitmap, destRect);
                            }
                            else
                            {
                                var destRect = new SKRect(0, 0, renderW, renderH);
                                canvas.DrawBitmap(bitmap, destRect);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Render error: {ex.Message}");
            }

            canvas.Restore();
        }
    }
}

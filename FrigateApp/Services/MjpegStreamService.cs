using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace FrigateApp.Services;

/// <summary>
/// Читает MJPEG-поток (multipart/x-mixed-replace; boundary=frame) и выдаёт кадры в UI.
/// </summary>
public class MjpegStreamService
{
    private static readonly byte[] FrameBoundary = { (byte)'\r', (byte)'\n', (byte)'-', (byte)'-', (byte)'f', (byte)'r', (byte)'a', (byte)'m', (byte)'e' };
    private static readonly byte[] HeadersEnd = { (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n' };
    private static readonly byte[] NextFrameStart = { (byte)'\r', (byte)'\n', (byte)'-', (byte)'-' };

    private readonly FrigateApiService _api;
    private CancellationTokenSource? _cts;
    private bool _running;

    public MjpegStreamService(FrigateApiService api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public event Action<Bitmap?>? FrameReady;
    public event Action<string?>? ErrorOccurred;

    public bool IsRunning => _running;

    public void Start(string cameraName)
    {
        Stop();
        _cts = new CancellationTokenSource();
        _running = true;
        _ = RunStreamAsync(cameraName, _cts.Token);
    }

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task RunStreamAsync(string cameraName, CancellationToken ct)
    {
        try
        {
            // Main-поток (1080p, 30 fps) для полноэкранного просмотра — больше ресурсов на открытую камеру
            await using var stream = await _api.GetMjpegStreamAsync(cameraName, fps: 30, height: 1080, ct).ConfigureAwait(false);
            await ParseMjpegStreamAsync(stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // нормальное завершение
        }
        catch (Exception ex)
        {
            _running = false;
            DispatchError(ex.Message);
        }
    }

    private async Task ParseMjpegStreamAsync(Stream stream, CancellationToken ct)
    {
        var readBuffer = new byte[32 * 1024];
        var data = new MemoryStream();

        while (_running && !ct.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), ct).ConfigureAwait(false);
            if (read <= 0) break;

            data.Write(readBuffer, 0, read);
            var buf = data.GetBuffer();
            var len = (int)data.Length;

            var boundary = IndexOf(buf, len, FrameBoundary);
            if (boundary < 0)
            {
                if (len > 1024 * 1024) data.SetLength(0);
                continue;
            }

            var afterBoundary = boundary + FrameBoundary.Length;
            var headersEnd = IndexOf(buf, len, HeadersEnd, afterBoundary);
            if (headersEnd < 0)
            {
                if (len > 1024 * 1024) data.SetLength(0);
                continue;
            }

            var jpegStart = headersEnd + HeadersEnd.Length;
            var nextBoundary = IndexOf(buf, len, NextFrameStart, jpegStart);
            if (nextBoundary < 0)
            {
                if (len > 2 * 1024 * 1024)
                {
                    data.SetLength(0);
                    data.Position = 0;
                }
                continue;
            }

            var jpegLength = nextBoundary - jpegStart;
            if (jpegLength > 0 && jpegLength < 5 * 1024 * 1024)
            {
                var jpegBytes = new byte[jpegLength];
                Buffer.BlockCopy(buf, jpegStart, jpegBytes, 0, jpegLength);
                DispatchFrame(jpegBytes);
            }

            var remaining = len - nextBoundary;
            if (remaining > 0)
            {
                var temp = new byte[remaining];
                Buffer.BlockCopy(buf, nextBoundary, temp, 0, remaining);
                data.SetLength(0);
                data.Write(temp, 0, remaining);
            }
            else
                data.SetLength(0);
        }

        _running = false;
    }

    private static int IndexOf(byte[] buffer, int length, byte[] pattern, int startIndex = 0)
    {
        for (var i = startIndex; i <= length - pattern.Length; i++)
        {
            var found = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (buffer[i + j] != pattern[j]) { found = false; break; }
            }
            if (found) return i;
        }
        return -1;
    }

    private void DispatchFrame(byte[] jpegBytes)
    {
        try
        {
            using var ms = new MemoryStream(jpegBytes);
            var bmp = new Bitmap(ms);
            Dispatcher.UIThread.Post(() => FrameReady?.Invoke(bmp));
        }
        catch
        {
            // пропускаем битый кадр
        }
    }

    private void DispatchError(string message)
    {
        Dispatcher.UIThread.Post(() => ErrorOccurred?.Invoke(message));
    }
}

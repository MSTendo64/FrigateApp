using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrigateApp.Services;

internal sealed class StreamNotFoundException : Exception { }

/// <summary>
/// Подключается к fMP4-потоку Frigate по WebSocket (wss) и записывает его в локальный файл.
/// LibVLC затем воспроизводит растущий файл как live-поток.
/// </summary>
public class WssFmp4StreamService : IDisposable
{
    private readonly FrigateApiService _api;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoopTask;
    private FileStream? _fileStream;
    private string? _filePath;
    private bool _fileReported;
    private bool _disposed;
    private const int FirstDataTimeoutSeconds = 20;

    /// <summary>Вызывается один раз, когда создан и заполнен первыми данными локальный файл с fMP4 потоком.</summary>
    public event Action<string>? FileReady;

    /// <summary>Сообщение об ошибке (например, проблемы с WebSocket).</summary>
    public event Action<string>? ErrorOccurred;

    public WssFmp4StreamService(FrigateApiService api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>
    /// Запустить приём потока. streamName — имя потока go2rtc из camera.live.streams (как в вебе).
    /// Метод возвращается сразу, приёма данных идёт в фоне.
    /// </summary>
    public Task StartAsync(string streamName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(streamName))
            throw new ArgumentException("Stream name is required.", nameof(streamName));

        if (_disposed)
            throw new ObjectDisposedException(nameof(WssFmp4StreamService));

        Stop();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = new ClientWebSocket();
        _fileReported = false;

        _receiveLoopTask = RunAsync(streamName, _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>Остановить поток и освободить ресурсы.</summary>
    public void Stop()
    {
        if (_disposed)
            return;

        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _webSocket?.Abort();
            _webSocket?.Dispose();
        }
        catch
        {
            // ignore
        }

        _webSocket = null;

        try
        {
            _fileStream?.Flush();
            _fileStream?.Dispose();
        }
        catch
        {
            // ignore
        }

        _fileStream = null;
    }

    private async Task RunAsync(string streamName, CancellationToken ct)
    {
        try
        {
            var wsUrl = _api.GetLiveMseWebSocketUrl(streamName);
            System.Diagnostics.Debug.WriteLine($"[WssFmp4] Connecting to {wsUrl}");

            if (_webSocket == null)
                return;

            var cookie = _api.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookie))
                _webSocket.Options.SetRequestHeader("Cookie", cookie);

            await _webSocket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
            await SendCodecRequestAsync(ct).ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(FirstDataTimeoutSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (_fileReported) return;
                ErrorOccurred?.Invoke("Таймаут ожидания потока");
                try { _cts?.Cancel(); } catch { }
            }, ct);

            await ReceiveLoopAsync(ct).ConfigureAwait(false);
        }
        catch (StreamNotFoundException)
        {
            string? fallback = null;
            if (streamName.EndsWith("_main", StringComparison.OrdinalIgnoreCase))
                fallback = streamName[..^5].TrimEnd('_');
            else if (!streamName.EndsWith("_sub", StringComparison.OrdinalIgnoreCase))
                fallback = streamName + "_main";
            if (!string.IsNullOrEmpty(fallback))
            {
                try { _webSocket?.Abort(); _webSocket?.Dispose(); } catch { }
                _webSocket = null;
                _webSocket = new ClientWebSocket();
                if (!string.IsNullOrEmpty(_api.GetCookieHeader()))
                    _webSocket.Options.SetRequestHeader("Cookie", _api.GetCookieHeader());
                await RunAsync(fallback, ct).ConfigureAwait(false);
                return;
            }
            ErrorOccurred?.Invoke("mse: stream not found");
        }
        catch (OperationCanceledException)
        {
            // нормальное завершение
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WssFmp4] Error: {ex}");
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            Stop();
        }
    }

    private async Task SendCodecRequestAsync(CancellationToken ct)
    {
        if (_webSocket == null)
            return;

        // Используем тот же набор кодеков, что и web-клиент MSE.
        var codecs = string.Join(",",
            "avc1.640029", // H.264 high 4.1
            "avc1.64002A", // H.264 high 4.2
            "avc1.640033", // H.264 high 5.1
            "hvc1.1.6.L153.B0", // H.265 main 5.1
            "mp4a.40.2", // AAC LC
            "mp4a.40.5", // AAC HE
            "flac",
            "opus"
        );

        var request = new
        {
            type = "mse",
            value = codecs
        };

        var json = JsonConvert.SerializeObject(request);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken: ct
        ).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_webSocket == null)
            return;

        var buffer = new byte[64 * 1024];

        while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            var ms = new MemoryStream();
            WebSocketReceiveResult result;

            do
            {
                result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "ok", ct)
                        .ConfigureAwait(false);
                    return;
                }

                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ms.Position = 0;

            if (result.MessageType == WebSocketMessageType.Text)
            {
                using var reader = new StreamReader(ms, Encoding.UTF8);
                var text = await reader.ReadToEndAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[WssFmp4] Text message: {text}");
                try
                {
                    var obj = JObject.Parse(text);
                    var type = obj["type"]?.ToString();
                    var value = obj["value"]?.ToString() ?? "";
                    if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.IndexOf("stream not found", StringComparison.OrdinalIgnoreCase) >= 0)
                            throw new StreamNotFoundException();
                        ErrorOccurred?.Invoke(value);
                        return;
                    }
                    // type "mse" — список кодеков от сервера, игнорируем
                }
                catch
                {
                    // не JSON — просто продолжаем
                }
                continue;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                await EnsureFileCreatedAsync().ConfigureAwait(false);

                if (_fileStream == null)
                    continue;

                await ms.CopyToAsync(_fileStream, ct).ConfigureAwait(false);
                await _fileStream.FlushAsync(ct).ConfigureAwait(false);

                if (!_fileReported && !string.IsNullOrEmpty(_filePath))
                {
                    _fileReported = true;
                    FileReady?.Invoke(_filePath);
                }
            }
        }
    }

    private Task EnsureFileCreatedAsync()
    {
        if (_fileStream != null)
            return Task.CompletedTask;

        var tempDir = Path.GetTempPath();
        var fileName = $"frigate_live_{Guid.NewGuid():N}.mp4";
        _filePath = Path.Combine(tempDir, fileName);

        _fileStream = new FileStream(
            _filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            useAsync: true);

        System.Diagnostics.Debug.WriteLine($"[WssFmp4] Writing fMP4 stream to {_filePath}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}


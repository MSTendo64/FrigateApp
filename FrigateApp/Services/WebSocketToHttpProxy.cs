using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FrigateApp.Services;

/// <summary>
/// HTTP прокси для проксирования WSS потока напрямую в LibVLC.
/// </summary>
public class WebSocketToHttpProxy : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _wsUrl;
    private readonly string? _cookie;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private bool _disposed;
    private bool _isRunning;

    /// <summary>HTTP URL для LibVLC (например http://localhost:8080/stream)</summary>
    public string HttpUrl { get; }

    /// <summary>Запущен ли прокси.</summary>
    public bool IsRunning => _isRunning;

    public WebSocketToHttpProxy(string wsUrl, int port = 0, string? cookie = null)
    {
        _wsUrl = wsUrl;
        _cookie = cookie;
        _port = port == 0 ? GetAvailablePort() : port;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/stream/");
        HttpUrl = $"http://localhost:{_port}/stream";
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Запустить прокси сервер.
    /// </summary>
    public async Task StartAsync()
    {
        if (_disposed || _isRunning) return;

        _cts = new CancellationTokenSource();
        _listener.Start();
        _isRunning = true;
        _listenTask = ListenLoopAsync(_cts.Token);

        Console.WriteLine($"[WS Proxy] Started at {HttpUrl}");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isRunning)
        {
            try
            {
                var context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, ct), ct);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WS Proxy] Listen error: {ex.Message}");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        
        // Заголовки для потокового видео
        response.Headers["Content-Type"] = "video/mp4";
        response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
        response.Headers["Connection"] = "keep-alive";
        response.Headers["Transfer-Encoding"] = "chunked";
        response.SendChunked = true;
        response.StatusCode = 200;

        Console.WriteLine($"[WS Proxy] Client connected");

        using var wsClient = new ClientWebSocket();
        wsClient.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        
        // Добавляем cookie для авторизации
        if (!string.IsNullOrEmpty(_cookie))
        {
            wsClient.Options.SetRequestHeader("Cookie", _cookie);
            Console.WriteLine($"[WS Proxy] Using cookie: {_cookie.Substring(0, Math.Min(20, _cookie.Length))}...");
        }

        try
        {
            // Подключаемся к WebSocket
            await wsClient.ConnectAsync(new Uri(_wsUrl), ct).ConfigureAwait(false);
            Console.WriteLine($"[WS Proxy] WebSocket connected");

            // Отправляем codec request (MSE protocol)
            await SendCodecRequestAsync(wsClient, ct).ConfigureAwait(false);

            // Проксирование данных
            var buffer = new byte[256 * 1024];
            var totalBytesSent = 0L;

            while (!ct.IsCancellationRequested && wsClient.State == WebSocketState.Open)
            {
                var result = await wsClient.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Console.WriteLine($"[WS Proxy] WebSocket closed");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Binary && result.Count > 0)
                {
                    await response.OutputStream.WriteAsync(buffer, 0, result.Count, ct).ConfigureAwait(false);
                    await response.OutputStream.FlushAsync(ct).ConfigureAwait(false);
                    
                    totalBytesSent += result.Count;
                    if (totalBytesSent % (512 * 1024) < 1024 && totalBytesSent > 0)
                    {
                        Console.WriteLine($"[WS Proxy] Sent {totalBytesSent / 1024} KB");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[WS Proxy] Request cancelled");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WS Proxy] Error: {ex.Message}");
        }
        finally
        {
            try { response.OutputStream.Close(); } catch { }
            try { response.Close(); } catch { }
            Console.WriteLine($"[WS Proxy] Client disconnected");
        }
    }

    private async Task SendCodecRequestAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var codecs = "avc1.640029,avc1.64002A,avc1.640033,hvc1.1.6.L153.B0,mp4a.40.2,mp4a.40.5,opus";
        var request = new { type = "mse", value = codecs };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
        var buffer = Encoding.UTF8.GetBytes(json);

        await ws.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            ct).ConfigureAwait(false);
    }

    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        try { _listener.Close(); } catch { }
        try { _listener.Abort(); } catch { }

        Console.WriteLine($"[WS Proxy] Stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

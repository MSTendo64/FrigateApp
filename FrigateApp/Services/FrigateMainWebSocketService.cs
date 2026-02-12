using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FrigateApp.Services;

/// <summary>
/// Подключение к главному WebSocket Frigate (ws://host/ws).
/// Поднимает руку с cookie frigate_token, получает JSON-сообщения с topic и payload (onConnect, camera_activity и т.д.).
/// </summary>
public class FrigateMainWebSocketService : IDisposable
{
    private readonly FrigateApiService _api;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private bool _disposed;

    public FrigateMainWebSocketService(FrigateApiService api)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    /// <summary>Получено текстовое сообщение: topic, payload (может быть JSON-строка или объект).</summary>
    public event Action<string, string>? MessageReceived;

    /// <summary>Подключение установлено (получен onConnect или первый пакет).</summary>
    public event Action? Connected;

    /// <summary>Ошибка или разрыв соединения.</summary>
    public event Action<string>? ErrorOccurred;

    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    /// <summary>Запустить подключение. Сообщения приходят в фоне.</summary>
    public void Start()
    {
        if (_disposed) return;
        Stop();
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        var cookie = _api.GetCookieHeader();
        if (!string.IsNullOrEmpty(cookie))
            _webSocket.Options.SetRequestHeader("Cookie", cookie);
        _receiveTask = RunAsync(_cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try
        {
            _webSocket?.Abort();
            _webSocket?.Dispose();
        }
        catch { }
        _webSocket = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var wsUrl = _api.GetMainWebSocketUrl();
        try
        {
            await _webSocket!.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
            Connected?.Invoke();
            var buffer = new byte[64 * 1024];
            while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await _webSocket.ReceiveAsync(segment, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", ct).ConfigureAwait(false);
                    break;
                }
                if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    try
                    {
                        var obj = JObject.Parse(json);
                        var topic = obj["topic"]?.ToString() ?? "";
                        var payload = obj["payload"]?.ToString() ?? obj["message"]?.ToString() ?? "";
                        MessageReceived?.Invoke(topic, payload);
                    }
                    catch
                    {
                        MessageReceived?.Invoke("", json);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(ex.Message);
        }
        finally
        {
            Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

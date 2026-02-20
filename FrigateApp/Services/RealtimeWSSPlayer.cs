using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace FrigateApp.Services;

/// <summary>
/// Realtime WSS плеер с непрерывной буферизацией и минимальной задержкой.
/// </summary>
public class RealtimeWSSPlayer : IDisposable
{
    private ClientWebSocket? _webSocket;
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private readonly ConcurrentQueue<byte[]> _frameQueue = new();
    private CancellationTokenSource? _cts;
    private bool _isPlaying;
    private MemoryStream? _currentStream;
    private readonly object _streamLock = new();
    private DateTime _lastFrameTime;

    /// <summary>Событие: получен новый кадр.</summary>
    public event Action<byte[]>? FrameReceived;

    /// <summary>Событие: ошибка.</summary>
    public event Action<string>? ErrorOccurred;

    /// <summary>MediaPlayer для привязки к VideoView.</summary>
    public MediaPlayer? MediaPlayer => _mediaPlayer;

    public RealtimeWSSPlayer()
    {
        // Инициализация LibVLC с оптимизацией для реального времени
        Core.Initialize();
        
        _libVLC = new LibVLC(
            "--no-audio",
            "--network-caching=50",
            "--live-caching=50",
            "--clock-jitter=0",
            "--file-caching=50",
            "--demux=mp4"
        );
        
        _mediaPlayer = new MediaPlayer(_libVLC);
    }

    /// <summary>
    /// Подключиться к WSS потоку.
    /// </summary>
    public async Task ConnectAsync(string wssUrl, string? cookie = null)
    {
        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        
        if (!string.IsNullOrEmpty(cookie))
            _webSocket.Options.SetRequestHeader("Cookie", cookie);
        
        _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        
        await _webSocket.ConnectAsync(new Uri(wssUrl), _cts.Token);
        
        _isPlaying = true;
        _lastFrameTime = DateTime.UtcNow;

        // Отправляем codec request
        await SendCodecRequestAsync(_cts.Token).ConfigureAwait(false);

        // Запускаем получение и воспроизведение
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
        _ = Task.Run(() => PlaybackLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task SendCodecRequestAsync(CancellationToken ct)
    {
        if (_webSocket == null) return;

        var codecs = "avc1.640029,avc1.64002A,avc1.640033,hvc1.1.6.L153.B0,mp4a.40.2,mp4a.40.5,opus";
        var request = new { type = "mse", value = codecs };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(request);
        var buffer = Encoding.UTF8.GetBytes(json);

        await _webSocket.SendAsync(
            new ArraySegment<byte>(buffer),
            WebSocketMessageType.Text,
            true,
            ct).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        if (_webSocket == null) return;

        var buffer = new byte[65536];
        var segmentBuffer = new MemoryStream();
        
        while (!ct.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            try
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), 
                    ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    _lastFrameTime = DateTime.UtcNow;
                    segmentBuffer.Write(buffer, 0, result.Count);
                    
                    // Проверяем полный fMP4 фрагмент (moof атом)
                    if (IsCompleteFragment(segmentBuffer.ToArray()))
                    {
                        var fragment = segmentBuffer.ToArray();
                        segmentBuffer.SetLength(0);
                        
                        _frameQueue.Enqueue(fragment);
                        FrameReceived?.Invoke(fragment);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"Receive error: {ex.Message}");
                break;
            }
        }
    }

    private bool IsCompleteFragment(byte[] data)
    {
        // Проверка на наличие moof (movie fragment) атома
        for (int i = 0; i < data.Length - 8; i++)
        {
            if (data[i + 4] == 'm' && data[i + 5] == 'o' && 
                data[i + 6] == 'o' && data[i + 7] == 'f')
            {
                return true;
            }
        }
        return false;
    }

    private async Task PlaybackLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _isPlaying)
        {
            if (_frameQueue.TryDequeue(out byte[]? fragment))
            {
                lock (_streamLock)
                {
                    _currentStream?.Dispose();
                    _currentStream = new MemoryStream(fragment);
                    
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            var media = new Media(_libVLC!, new StreamMediaInput(_currentStream));
                            _mediaPlayer?.Play(media);
                        }
                        catch (Exception ex)
                        {
                            ErrorOccurred?.Invoke($"Playback error: {ex.Message}");
                        }
                    });
                }
                
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(5, ct).ConfigureAwait(false);
            }
        }
    }

    public bool IsStreamAlive()
    {
        var elapsed = DateTime.UtcNow - _lastFrameTime;
        return elapsed.TotalSeconds < 10;
    }

    public void AttachToVideoView(LibVLCSharp.Avalonia.VideoView videoView)
    {
        if (videoView != null && _mediaPlayer != null)
            videoView.MediaPlayer = _mediaPlayer;
    }

    public void Stop()
    {
        _isPlaying = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        
        _webSocket?.Abort();
        _webSocket?.Dispose();
        _webSocket = null;
        
        _currentStream?.Dispose();
        _currentStream = null;
    }

    public void Dispose()
    {
        Stop();
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _libVLC?.Dispose();
        _libVLC = null;
    }
}

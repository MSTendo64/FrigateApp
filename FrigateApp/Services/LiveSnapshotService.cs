using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace FrigateApp.Services;

/// <summary>
/// Сервис для получения live-снапшотов с камеры.
/// </summary>
public class LiveSnapshotService : IDisposable
{
    private readonly FrigateApiService _api;
    private CancellationTokenSource? _cts;
    private Task? _refreshTask;
    private bool _disposed;
    private DateTime _lastFrameTime;
    private readonly int _updateIntervalMs;
    private readonly int _snapshotHeight;

    /// <summary>Событие: получен новый снапшот.</summary>
    public event Action<Bitmap?>? SnapshotUpdated;

    /// <summary>Событие: ошибка.</summary>
    public event Action<string>? ErrorOccurred;

    public LiveSnapshotService(FrigateApiService api, int updateIntervalMs = 500, int snapshotHeight = 720)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _updateIntervalMs = updateIntervalMs;
        _snapshotHeight = snapshotHeight;
    }

    /// <summary>
    /// Запустить обновление снапшотов.
    /// </summary>
    public void Start(string cameraName)
    {
        if (_disposed) return;
        Stop();

        _cts = new CancellationTokenSource();
        _refreshTask = RefreshLoopAsync(cameraName, _cts.Token);
    }

    private async Task RefreshLoopAsync(string cameraName, CancellationToken ct)
    {
        var consecutiveErrors = 0;
        var maxErrors = 10;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                sw.Restart();
                var bytes = await _api.GetLatestFrameBytesAsync(cameraName, _snapshotHeight, ct).ConfigureAwait(false);
                var downloadTime = sw.ElapsedMilliseconds;
                
                if (bytes != null && bytes.Length > 0)
                {
                    consecutiveErrors = 0;
                    _lastFrameTime = DateTime.UtcNow;

                    Bitmap? bitmap = null;
                    try
                    {
                        using var ms = new MemoryStream(bytes);
                        bitmap = new Bitmap(ms);
                        System.Diagnostics.Debug.WriteLine($"[Snapshot] {cameraName}: {bytes.Length/1024} KB, download={downloadTime}ms, total={sw.ElapsedMilliseconds}ms");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Snapshot] Decode error: {ex.Message}");
                    }

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        SnapshotUpdated?.Invoke(bitmap);
                    }, DispatcherPriority.Background);
                }
                else
                {
                    consecutiveErrors++;
                    if (consecutiveErrors >= maxErrors)
                    {
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ErrorOccurred?.Invoke("Нет данных от камеры");
                        });
                    }
                }

                // Рассчитываем задержку для поддержания FPS
                var frameTime = _updateIntervalMs - sw.ElapsedMilliseconds;
                if (frameTime > 0)
                {
                    await Task.Delay((int)frameTime, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                System.Diagnostics.Debug.WriteLine($"[Snapshot] Error: {ex.Message}");
                
                if (consecutiveErrors >= maxErrors)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ErrorOccurred?.Invoke(ex.Message);
                    });
                }
                
                try { await Task.Delay(_updateIntervalMs * 2, ct).ConfigureAwait(false); }
                catch { break; }
            }
        }
    }

    public bool IsStreamAlive()
    {
        var elapsed = DateTime.UtcNow - _lastFrameTime;
        return elapsed.TotalSeconds < 10;
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

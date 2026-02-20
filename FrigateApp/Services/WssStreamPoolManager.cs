using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FrigateApp.Services;

/// <summary>
/// Диспетчер WSS потоков для сетки камер.
/// Ограничивает количество одновременных потоков для снижения нагрузки на CPU.
/// </summary>
public sealed class WssStreamPoolManager : IDisposable
{
    private readonly SemaphoreSlim _maxStreamsSemaphore;
    private readonly ConcurrentDictionary<string, WssStreamInfo> _activeStreams = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>Максимальное количество одновременных WSS потоков (по умолчанию 4).</summary>
    public int MaxConcurrentStreams
    {
        get => _maxStreamsSemaphore.CurrentCount + _runningStreams;
        set
        {
            if (value < 1) value = 1;
            if (value > 20) value = 20;

            lock (_lock)
            {
                var currentMax = _maxStreamsSemaphore.CurrentCount + _runningStreams;
                var delta = value - currentMax;
                if (delta > 0)
                    _maxStreamsSemaphore.Release(delta);
                else if (delta < 0)
                {
                    // Уменьшаем максимум - новые запросы будут блокироваться
                    // Это не прерывает текущие потоки, только ограничивает новые
                }
            }
        }
    }

    private int _runningStreams;

    /// <summary>Количество активных потоков.</summary>
    public int ActiveStreamsCount => _activeStreams.Count;

    public WssStreamPoolManager(int maxConcurrentStreams = 4)
    {
        _maxStreamsSemaphore = new SemaphoreSlim(maxConcurrentStreams, maxConcurrentStreams);
    }

    /// <summary>
    /// Запустить поток для камеры. Если достигнут лимит — ждать освобождения.
    /// </summary>
    public async Task<WssStreamHandle?> AcquireStreamAsync(
        string cameraName,
        string streamName,
        FrigateApiService api,
        Action<string>? onFileReady,
        Action<string>? onError,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;
        if (string.IsNullOrEmpty(cameraName)) return null;

        // Проверяем, есть ли уже активный поток для этой камеры
        if (_activeStreams.TryGetValue(cameraName, out var existing))
        {
            existing.RefreshTimeout();
            return new WssStreamHandle(cameraName, this, existing.Stream);
        }

        // Ждём доступного слота
        try
        {
            await _maxStreamsSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                _maxStreamsSemaphore.Release();
                return null;
            }

            // Повторная проверка после получения семафора
            if (_activeStreams.TryGetValue(cameraName, out existing))
            {
                _maxStreamsSemaphore.Release();
                existing.RefreshTimeout();
                return new WssStreamHandle(cameraName, this, existing.Stream);
            }

            // Создаём новый поток
            var wssStream = new WssFmp4StreamService(api);
            var streamInfo = new WssStreamInfo(wssStream);
            _activeStreams[cameraName] = streamInfo;
            _runningStreams++;

            // Подписываемся на события
            wssStream.FileReady += (path) =>
            {
                if (_activeStreams.TryGetValue(cameraName, out var info))
                    info.LastFileReadyTime = DateTime.UtcNow;
                onFileReady?.Invoke(path);
            };

            wssStream.ErrorOccurred += (msg) =>
            {
                onError?.Invoke(msg);
            };

            // Запускаем поток
            _ = wssStream.StartAsync(streamName, cancellationToken);

            System.Diagnostics.Debug.WriteLine($"[WssPool] Stream started: {cameraName} ({streamName}), active: {_activeStreams.Count}");

            return new WssStreamHandle(cameraName, this, wssStream);
        }
    }

    /// <summary>
    /// Освободить поток камеры. Поток не закрывается сразу, а получает таймаут на закрытие.
    /// </summary>
    public void ReleaseStream(string cameraName, WssFmp4StreamService stream)
    {
        if (_disposed || string.IsNullOrEmpty(cameraName)) return;

        lock (_lock)
        {
            if (!_activeStreams.TryGetValue(cameraName, out var existing))
                return;

            if (!ReferenceEquals(existing.Stream, stream))
                return; // Уже заменён на новый

            existing.MarkForDisposal();
            System.Diagnostics.Debug.WriteLine($"[WssPool] Stream released: {cameraName}, will dispose in {WssStreamInfo.DisposalDelaySeconds}s");

            // Запускаем таймер на закрытие
            _ = Task.Delay(TimeSpan.FromSeconds(WssStreamInfo.DisposalDelaySeconds)).ContinueWith(_ =>
            {
                lock (_lock)
                {
                    if (_disposed) return;

                    if (!_activeStreams.TryGetValue(cameraName, out var info))
                        return;

                    if (!info.IsMarkedForDisposal || info.LastActivityTime > DateTime.UtcNow.AddSeconds(-2))
                        return; // Была активность, не закрываем

                    // Закрываем поток
                    System.Diagnostics.Debug.WriteLine($"[WssPool] Stream disposed: {cameraName}");

                    try
                    {
                        info.Stream.FileReady -= info.FileReadyHandler;
                        info.Stream.ErrorOccurred -= info.ErrorOccurredHandler;
                        info.Stream.Stop();
                        info.Stream.Dispose();
                    }
                    catch { }

                    _activeStreams.TryRemove(cameraName, out var _);
                    _runningStreams--;
                    _maxStreamsSemaphore.Release();
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Остановить все потоки.
    /// </summary>
    public void StopAll()
    {
        lock (_lock)
        {
            var streamsToStop = new List<WssStreamInfo>(_activeStreams.Values);
            foreach (var info in streamsToStop)
            {
                try
                {
                    info.Stream.Stop();
                    info.Stream.Dispose();
                }
                catch { }
            }
            _activeStreams.Clear();
            _runningStreams = 0;
            // Семафор не сбрасываем - он автоматически освободится при Stop
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
        _maxStreamsSemaphore.Dispose();
    }

    private class WssStreamInfo
    {
        public const int DisposalDelaySeconds = 3;
        public WssFmp4StreamService Stream { get; }
        public DateTime LastFileReadyTime { get; set; } = DateTime.MinValue;
        public DateTime LastActivityTime { get; set; } = DateTime.UtcNow;
        public bool IsMarkedForDisposal { get; private set; }

        public Action<string>? FileReadyHandler { get; }
        public Action<string>? ErrorOccurredHandler { get; }

        public WssStreamInfo(WssFmp4StreamService stream)
        {
            Stream = stream;
            LastActivityTime = DateTime.UtcNow;

            // Сохраняем обработчики для последующей отписки
            FileReadyHandler = (path) => LastActivityTime = DateTime.UtcNow;
            ErrorOccurredHandler = (msg) => LastActivityTime = DateTime.UtcNow;

            Stream.FileReady += FileReadyHandler;
            Stream.ErrorOccurred += ErrorOccurredHandler;
        }

        public void RefreshTimeout()
        {
            LastActivityTime = DateTime.UtcNow;
            IsMarkedForDisposal = false;
        }

        public void MarkForDisposal()
        {
            IsMarkedForDisposal = true;
        }
    }
}

/// <summary>
/// Дескриптор активного WSS потока.
/// При уничтожении автоматически возвращает поток в пул.
/// </summary>
public sealed class WssStreamHandle : IDisposable
{
    private readonly string _cameraName;
    private readonly WssStreamPoolManager _pool;
    private readonly WssFmp4StreamService _stream;
    private bool _disposed;

    public WssFmp4StreamService Stream => _stream;

    public WssStreamHandle(string cameraName, WssStreamPoolManager pool, WssFmp4StreamService stream)
    {
        _cameraName = cameraName;
        _pool = pool;
        _stream = stream;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pool.ReleaseStream(_cameraName, _stream);
    }
}

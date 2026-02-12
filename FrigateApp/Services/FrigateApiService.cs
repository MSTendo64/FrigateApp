using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FrigateApp.Models;
using Newtonsoft.Json;

namespace FrigateApp.Services;

/// <summary>
/// Клиент Frigate API: авторизация, конфиг, превью камер.
/// Базовый URL: например https://frigate:8971/api (без завершающего слэша для BaseAddress).
/// </summary>
public class FrigateApiService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly CookieContainer? _cookieContainer;
    
    // Кеширование конфигурации (обновляется раз в минуту)
    private FrigateConfigResponse? _cachedConfig;
    private DateTime _configCacheTime = DateTime.MinValue;
    private readonly TimeSpan _configCacheDuration = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _configLock = new(1, 1);
    
    // Ограничение параллельных запросов превью камер
    private readonly SemaphoreSlim _previewThrottle = new(10, 10); // Макс 10 одновременных запросов

    /// <summary>Базовый URL API (без завершающего слэша). Для ключа профиля при сохранении настроек.</summary>
    public string BaseUrl => _baseUrl;

    public FrigateApiService(string serverBaseUrl, HttpClient? http = null)
    {
        _baseUrl = serverBaseUrl.TrimEnd('/');
        if (http != null)
        {
            _http = http;
            _cookieContainer = null;
        }
        else
        {
            _cookieContainer = new CookieContainer();
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                MaxConnectionsPerServer = 20, // Увеличено для параллельных запросов
                UseCookies = true,
                CookieContainer = _cookieContainer,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (_, _, _, _) => true
                },
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ResponseDrainTimeout = TimeSpan.FromSeconds(3)
            };
            
            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri(_baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(30),
                DefaultRequestVersion = HttpVersion.Version20, // HTTP/2 для multiplexing
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            
            // Добавляем заголовки для лучшего сжатия
            _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate");
        }
    }

    /// <summary>
    /// POST /api/login — авторизация. При успехе куки сохраняются в HttpClient.
    /// </summary>
    public async Task LoginAsync(string user, string password, CancellationToken ct = default)
    {
        var body = new { user, password };
        var json = JsonConvert.SerializeObject(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(HttpMethod.Post, "login") { Content = content };
        var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var msg = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"Ошибка входа: {response.StatusCode}. {msg}");
        }
    }

    /// <summary>
    /// GET /api/config — конфигурация (в т.ч. список камер). Требует авторизации.
    /// Кешируется на 1 минуту для снижения нагрузки.
    /// </summary>
    public async Task<FrigateConfigResponse> GetConfigAsync(CancellationToken ct = default)
    {
        // Проверяем кеш
        if (_cachedConfig != null && (DateTime.UtcNow - _configCacheTime) < _configCacheDuration)
        {
            return _cachedConfig;
        }

        // Блокируем для избежания дублирующих запросов
        await _configLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Двойная проверка после получения блокировки
            if (_cachedConfig != null && (DateTime.UtcNow - _configCacheTime) < _configCacheDuration)
            {
                return _cachedConfig;
            }

            var configJson = await _http.GetStringAsync("config", ct).ConfigureAwait(false);
            var config = JsonConvert.DeserializeObject<FrigateConfigResponse>(configJson);
            _cachedConfig = config ?? new FrigateConfigResponse();
            _configCacheTime = DateTime.UtcNow;
            return _cachedConfig;
        }
        finally
        {
            _configLock.Release();
        }
    }
    
    /// <summary>
    /// Очистить кеш конфигурации (для принудительного обновления).
    /// </summary>
    public void InvalidateConfigCache()
    {
        _cachedConfig = null;
        _configCacheTime = DateTime.MinValue;
    }

    /// <summary>
    /// Заголовок Cookie для запросов к тому же хосту (например WebSocket /live/mse/api/ws).
    /// Нужен для авторизации wss: nginx auth_request проверяет cookie frigate_token.
    /// </summary>
    public string GetCookieHeader()
    {
        if (_cookieContainer == null) return "";
        var uri = new Uri(_baseUrl.TrimEnd('/'));
        var cookieHeader = _cookieContainer.GetCookieHeader(uri);
        return cookieHeader ?? "";
    }

    /// <summary>
    /// URL WebSocket для MSE-потока — как в вебе: baseUrl (http→ws) + live/mse/api/ws?src={streamName}.
    /// streamName — имя потока go2rtc из camera.live.streams (например cam1_main, cam1_sub).
    /// </summary>
    public string GetLiveMseWebSocketUrl(string streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName))
            throw new ArgumentException("Stream name is required.", nameof(streamName));
        var wsBase = GetWebSocketBaseUrl();
        return $"{wsBase}/live/mse/api/ws?src={Uri.EscapeDataString(streamName)}";
    }

    /// <summary>Базовый URL для WebSocket (ws/wss + хост без /api).</summary>
    private string GetWebSocketBaseUrl()
    {
        var url = _baseUrl.TrimEnd('/');
        if (url.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            url = "ws://" + url.Substring("http://".Length);
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "wss://" + url.Substring("https://".Length);
        return url.TrimEnd('/');
    }

    /// <summary>
    /// Имя потока для live: из camera.live.streams по ключу (sub/main) или fallback cameraName_sub / cameraName_main.
    /// Как в вебе: streamName = values из live.streams, для плиток — sub, для полноэкрана — main.
    /// </summary>
    public static string GetStreamNameForLive(CameraConfig? camera, string cameraName, bool useSubStream)
    {
        var streams = camera?.Live?.Streams;
        if (streams != null && streams.Count > 0)
        {
            var key = useSubStream ? "sub" : "main";
            if (streams.TryGetValue(key, out var name) && !string.IsNullOrEmpty(name))
                return name;
            var values = streams.Values.ToList();
            if (useSubStream && values.Count > 0)
                return values[0];
            if (!useSubStream && values.Count > 0)
                return values[values.Count - 1];
        }
        // Без конфига: для main часто поток под именем камеры (cam1), при необходимости WssFmp4 сделает повтор с _main
        return useSubStream ? $"{cameraName}_sub" : cameraName;
    }

    /// <summary>
    /// URL главного WebSocket Frigate (ws://host/ws или wss://host/ws).
    /// Подключение с cookie frigate_token; сервер присылает JSON с topic/payload (onConnect, camera_activity и т.д.).
    /// </summary>
    public string GetMainWebSocketUrl() => $"{GetWebSocketBaseUrl()}/ws";

    /// <summary>
    /// GET /api/profile — профиль и разрешённые камеры. Требует авторизации.
    /// </summary>
    public async Task<ProfileResponse> GetProfileAsync(CancellationToken ct = default)
    {
        var profileJson = await _http.GetStringAsync("profile", ct).ConfigureAwait(false);
        var profile = JsonConvert.DeserializeObject<ProfileResponse>(profileJson);
        return profile ?? new ProfileResponse();
    }

    /// <summary>
    /// URL последнего кадра камеры: GET /api/{cameraName}/latest.jpg?h=...
    /// </summary>
    public string GetLatestFrameUrl(string cameraName, int height = 0) =>
        height > 0
            ? $"{_baseUrl}/{Uri.EscapeDataString(cameraName)}/latest.jpg?h={height}"
            : $"{_baseUrl}/{Uri.EscapeDataString(cameraName)}/latest.jpg";

    /// <summary>
    /// Скачать байты последнего кадра камеры (с учётом куков авторизации).
    /// height: 480 для sub-потока (превью), 1080 для main-потока (полный размер), 0 = по умолчанию.
    /// Использует throttling для ограничения параллельных запросов.
    /// </summary>
    public async Task<byte[]?> GetLatestFrameBytesAsync(string cameraName, int height = 480, CancellationToken ct = default)
    {
        await _previewThrottle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var url = height > 0
                ? $"{Uri.EscapeDataString(cameraName)}/latest.jpg?h={height}"
                : $"{Uri.EscapeDataString(cameraName)}/latest.jpg";
            
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10)); // Таймаут для отдельного запроса
            
            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            
            return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Таймаут отдельного запроса, не общая отмена
            System.Diagnostics.Debug.WriteLine($"[FrigateAPI] Timeout getting preview for {cameraName}");
            return null;
        }
        finally
        {
            _previewThrottle.Release();
        }
    }

    /// <summary>
    /// URL страницы go2rtc WebRTC для встраивания в WebView. Базовый URL без /api.
    /// </summary>
    public string GetLiveStreamPageUrl(string cameraName)
    {
        var baseWithoutApi = _baseUrl.EndsWith("/api", StringComparison.OrdinalIgnoreCase)
            ? _baseUrl[..^4]
            : _baseUrl.TrimEnd('/');
        return $"{baseWithoutApi.TrimEnd('/')}/live/webrtc/webrtc.html?src={Uri.EscapeDataString(cameraName)}";
    }

    /// <summary>
    /// Список имён камер из конфига (пересечение с allowed_cameras, если есть).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCameraNamesAsync(CancellationToken ct = default)
    {
        var config = await GetConfigAsync(ct).ConfigureAwait(false);
        var profile = await GetProfileAsync(ct).ConfigureAwait(false);

        var fromConfig = (config.Cameras?.Keys ?? Enumerable.Empty<string>()).ToList();
        var allowed = profile.AllowedCameras;

        if (allowed == null || allowed.Count == 0)
            return new List<string>(fromConfig);

        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var name in fromConfig)
        {
            if (set.Contains(name))
                list.Add(name);
        }
        return list;
    }

    // ————— Events API —————

    /// <summary>GET /api/events — список событий с фильтрами.</summary>
    public async Task<List<EventDto>> GetEventsAsync(
        string cameras = "all",
        string labels = "all",
        double? after = null,
        double? before = null,
        int limit = 100,
        string sort = "date_desc",
        CancellationToken ct = default)
    {
        var query = new List<string> { $"cameras={Uri.EscapeDataString(cameras)}", $"labels={Uri.EscapeDataString(labels)}", $"limit={limit}", $"sort={Uri.EscapeDataString(sort)}" };
        if (after.HasValue) query.Add($"after={after.Value}");
        if (before.HasValue) query.Add($"before={before.Value}");
        var url = "events?" + string.Join("&", query);
        var listJson = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var list = JsonConvert.DeserializeObject<List<EventDto>>(listJson);
        return list ?? new List<EventDto>();
    }

    /// <summary>GET /api/events/summary — сводка по дням (для таймлайна).</summary>
    public async Task<List<EventsSummaryItemDto>> GetEventsSummaryAsync(CancellationToken ct = default)
    {
        var listJson = await _http.GetStringAsync("events/summary", ct).ConfigureAwait(false);
        var list = JsonConvert.DeserializeObject<List<EventsSummaryItemDto>>(listJson);
        return list ?? new List<EventsSummaryItemDto>();
    }

    /// <summary>GET /api/events/{id} — одно событие.</summary>
    public async Task<EventDto?> GetEventByIdAsync(string eventId, CancellationToken ct = default)
    {
        var eventJson = await _http.GetStringAsync($"events/{Uri.EscapeDataString(eventId)}", ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<EventDto>(eventJson);
    }

    /// <summary>URL снимка события (с куками). GET /api/events/{id}/snapshot.jpg</summary>
    public string GetEventSnapshotUrl(string eventId) =>
        $"{_baseUrl}/events/{Uri.EscapeDataString(eventId)}/snapshot.jpg";

    /// <summary>URL миниатюры события. GET /api/events/{id}/thumbnail.jpg</summary>
    public string GetEventThumbnailUrl(string eventId) =>
        $"{_baseUrl}/events/{Uri.EscapeDataString(eventId)}/thumbnail.jpg";

    /// <summary>URL клипа события. GET /api/events/{id}/clip.mp4</summary>
    public string GetEventClipUrl(string eventId) =>
        $"{_baseUrl}/events/{Uri.EscapeDataString(eventId)}/clip.mp4";

    /// <summary>Скачать байты миниатюры события (с авторизацией).</summary>
    public async Task<byte[]?> GetEventThumbnailBytesAsync(string eventId, CancellationToken ct = default)
    {
        var url = $"events/{Uri.EscapeDataString(eventId)}/thumbnail.jpg";
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    // ————— Recordings API —————

    /// <summary>GET /api/recordings/summary — по каким дням есть записи (cameras=all).</summary>
    public async Task<Dictionary<string, bool>> GetRecordingsSummaryAsync(string cameras = "all", CancellationToken ct = default)
    {
        var url = "recordings/summary?cameras=" + Uri.EscapeDataString(cameras);
        var dictJson = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var dict = JsonConvert.DeserializeObject<Dictionary<string, bool>>(dictJson);
        return dict ?? new Dictionary<string, bool>();
    }

    /// <summary>GET /api/{camera}/recordings/summary — почасовая сводка по камере.</summary>
    public async Task<List<RecordingsDaySummaryDto>> GetCameraRecordingsSummaryAsync(string cameraName, CancellationToken ct = default)
    {
        var url = $"{Uri.EscapeDataString(cameraName)}/recordings/summary";
        var listJson = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var list = JsonConvert.DeserializeObject<List<RecordingsDaySummaryDto>>(listJson);
        return list ?? new List<RecordingsDaySummaryDto>();
    }

    /// <summary>GET /api/{camera}/recordings — сегменты записей в диапазоне.</summary>
    public async Task<List<RecordingSegmentDto>> GetCameraRecordingsAsync(string cameraName, double after, double before, CancellationToken ct = default)
    {
        var url = $"{Uri.EscapeDataString(cameraName)}/recordings?after={after}&before={before}";
        var listJson = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var list = JsonConvert.DeserializeObject<List<RecordingSegmentDto>>(listJson);
        return list ?? new List<RecordingSegmentDto>();
    }

    /// <summary>URL HLS плейлиста VOD для диапазона. Append /master.m3u8 для воспроизведения.</summary>
    public string GetVodPlaylistUrl(string cameraName, double startTs, double endTs) =>
        $"{_baseUrl}/vod/{Uri.EscapeDataString(cameraName)}/start/{startTs}/end/{endTs}";

    /// <summary>URL клипа MP4 по диапазону (для скачивания/просмотра).</summary>
    public string GetRecordingClipUrl(string cameraName, double startTs, double endTs) =>
        $"{_baseUrl}/{Uri.EscapeDataString(cameraName)}/start/{startTs}/end/{endTs}/clip.mp4";

    /// <summary>URL снимка записи в заданный момент (frame_time — unix). GET /:camera/recordings/:frame_time/snapshot.jpg</summary>
    public string GetRecordingSnapshotUrl(string cameraName, double frameTime) =>
        $"{_baseUrl}/{Uri.EscapeDataString(cameraName)}/recordings/{(long)Math.Floor(frameTime)}/snapshot.jpg";

    /// <summary>Скачать байты снимка записи в заданный момент (с авторизацией).</summary>
    public async Task<byte[]?> GetRecordingSnapshotBytesAsync(string cameraName, double frameTime, CancellationToken ct = default)
    {
        var url = $"{Uri.EscapeDataString(cameraName)}/recordings/{(long)Math.Floor(frameTime)}/snapshot.jpg";
        var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }
}

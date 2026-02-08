using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using FrigateApp.Models;

namespace FrigateApp.Services;

/// <summary>
/// Локальное хранилище: профили пользователей и зумы камер в JSON-файлах в папке приложения.
/// Папка: %AppData%\FrigateApp (Windows) или ~/.config/FrigateApp (Linux). Данные переживают закрытие приложения.
/// </summary>
public class UserPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _dir;
    private readonly string _filePath;
    private readonly string _cameraZoomsPath;
    private readonly string _tileScalePath;
    private List<SavedProfile> _profiles = new();
    // Ключ хранилища: profileKey + "|" + groupId (зум уникален для каждой группы камер)
    private Dictionary<string, Dictionary<string, CameraZoomState>> _cameraZoomsByProfileAndGroup = new();
    private bool _cameraZoomsLoaded;
    // Масштаб плиток для каждого профиля
    private Dictionary<string, double> _tileScaleByProfile = new();
    private bool _tileScaleLoaded;

    public UserPreferencesService()
    {
        _dir = GetConfigDirectory();
        Directory.CreateDirectory(_dir);
        _filePath = Path.Combine(_dir, "profiles.json");
        _cameraZoomsPath = Path.Combine(_dir, "camera-zooms.json");
        _tileScalePath = Path.Combine(_dir, "tile-scale.json");
    }

    /// <summary>Папка локального хранилища (профили, зумы камер).</summary>
    public string StorageDirectory => _dir;

    /// <summary>Надёжно определяет каталог для настроек (на Linux при пустом ApplicationData использует ~/.config/FrigateApp).</summary>
    private static string GetConfigDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            var dir = Path.GetFullPath(Path.Combine(appData, "FrigateApp"));
            if (!File.Exists(dir))
                return dir;
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                return Path.Combine(home, ".config", "FrigateApp");
        }
        var fallback = Path.Combine(Path.GetTempPath(), "FrigateApp");
        return Path.GetFullPath(fallback);
    }

    public IReadOnlyList<SavedProfile> Profiles
    {
        get
        {
            LoadIfNeeded();
            return _profiles;
        }
    }

    public void LoadIfNeeded()
    {
        if (_profiles.Count > 0 && File.Exists(_filePath))
            return;
        if (!File.Exists(_filePath))
        {
            _profiles = new List<SavedProfile>();
            return;
        }
        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<SavedProfile>>(json);
            _profiles = list ?? new List<SavedProfile>();
        }
        catch
        {
            _profiles = new List<SavedProfile>();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_profiles, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // игнорируем ошибки записи
        }
    }

    public void AddOrUpdate(SavedProfile profile)
    {
        LoadIfNeeded();
        var existing = _profiles.FindIndex(p =>
            string.Equals(p.ServerUrl, profile.ServerUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Username, profile.Username, StringComparison.Ordinal));
        if (existing >= 0)
            _profiles[existing] = profile;
        else
            _profiles.Add(profile);
        Save();
    }

    public void Remove(string serverUrl, string username)
    {
        LoadIfNeeded();
        _profiles.RemoveAll(p =>
            string.Equals(p.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Username, username, StringComparison.Ordinal));
        Save();
    }

    public SavedProfile? Find(string serverUrl, string username)
    {
        LoadIfNeeded();
        return _profiles.Find(p =>
            string.Equals(p.ServerUrl, serverUrl, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Username, username, StringComparison.Ordinal));
    }

    /// <summary>Ключ профиля: сервер + пользователь. Зумы хранятся отдельно для каждого пользователя (у каждого свои).</summary>
    public static string GetProfileKey(string serverUrl, string username)
    {
        var server = (serverUrl ?? "").Trim().ToLowerInvariant();
        var user = (username ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(user)) user = "__default__";
        return server + "|" + user;
    }

    private void LoadCameraZoomsIfNeeded()
    {
        if (_cameraZoomsLoaded) return;
        _cameraZoomsLoaded = true;
        if (!File.Exists(_cameraZoomsPath))
        {
            _cameraZoomsByProfileAndGroup = new Dictionary<string, Dictionary<string, CameraZoomState>>();
            return;
        }
        try
        {
            var json = File.ReadAllText(_cameraZoomsPath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, CameraZoomState>>>(json, JsonOptions);
            _cameraZoomsByProfileAndGroup = raw ?? new Dictionary<string, Dictionary<string, CameraZoomState>>();
        }
        catch
        {
            _cameraZoomsByProfileAndGroup = new Dictionary<string, Dictionary<string, CameraZoomState>>();
        }
    }

    /// <summary>Ключ хранилища зумов: профиль + группа (уникальный зум на каждую группу камер).</summary>
    private static string GetStorageKey(string profileKey, string groupId) =>
        (profileKey ?? "") + "|" + (groupId ?? "");

    /// <summary>Зумы камер для профиля и группы (cameraName -> state). Для «Все камеры» (groupId == null) возвращает пустой словарь.</summary>
    public IReadOnlyDictionary<string, CameraZoomState> GetCameraZooms(string profileKey, string? groupId)
    {
        if (string.IsNullOrEmpty(groupId)) return new Dictionary<string, CameraZoomState>();
        LoadCameraZoomsIfNeeded();
        var key = GetStorageKey(profileKey, groupId);
        if (_cameraZoomsByProfileAndGroup.TryGetValue(key, out var dict))
            return dict;
        return new Dictionary<string, CameraZoomState>();
    }

    /// <summary>Сохранить зум одной камеры для профиля и группы. groupId не должен быть null.</summary>
    public void SaveCameraZoom(string profileKey, string? groupId, string cameraName, CameraZoomState state)
    {
        if (string.IsNullOrEmpty(groupId)) return;
        LoadCameraZoomsIfNeeded();
        var key = GetStorageKey(profileKey, groupId);
        if (!_cameraZoomsByProfileAndGroup.TryGetValue(key, out var dict))
        {
            dict = new Dictionary<string, CameraZoomState>();
            _cameraZoomsByProfileAndGroup[key] = dict;
        }
        dict[cameraName] = new CameraZoomState
        {
            ZoomLevel = state.ZoomLevel,
            PanX = state.PanX,
            PanY = state.PanY
        };
        try
        {
            var json = JsonSerializer.Serialize(_cameraZoomsByProfileAndGroup, JsonOptions);
            File.WriteAllText(_cameraZoomsPath, json);
        }
        catch
        {
            // игнорируем ошибки записи
        }
    }

    /// <summary>Загрузить масштаб плиток если еще не загружен.</summary>
    private void LoadTileScaleIfNeeded()
    {
        if (_tileScaleLoaded) return;
        _tileScaleLoaded = true;
        if (!File.Exists(_tileScalePath)) return;
        try
        {
            var json = File.ReadAllText(_tileScalePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, double>>(json, JsonOptions);
            _tileScaleByProfile = raw ?? new Dictionary<string, double>();
        }
        catch
        {
            _tileScaleByProfile = new Dictionary<string, double>();
        }
    }

    /// <summary>Получить масштаб плиток для профиля (по умолчанию 1.0).</summary>
    public double GetTileScale(string profileKey)
    {
        LoadTileScaleIfNeeded();
        return _tileScaleByProfile.TryGetValue(profileKey, out var scale) ? scale : 1.0;
    }

    /// <summary>Сохранить масштаб плиток для профиля.</summary>
    public void SaveTileScale(string profileKey, double scale)
    {
        LoadTileScaleIfNeeded();
        _tileScaleByProfile[profileKey] = scale;
        try
        {
            var json = JsonSerializer.Serialize(_tileScaleByProfile, JsonOptions);
            File.WriteAllText(_tileScalePath, json);
        }
        catch
        {
            // игнорируем ошибки записи
        }
    }

}

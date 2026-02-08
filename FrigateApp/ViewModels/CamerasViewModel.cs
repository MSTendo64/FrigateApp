using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class CamerasViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<SidebarItemViewModel> _sidebarItems = new();

    [ObservableProperty]
    private ObservableCollection<CameraItemViewModel> _cameras = new();

    [ObservableProperty]
    private string _title = "Камеры";

    [ObservableProperty]
    private string _userName = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _loadError = "";

    /// <summary>Выбрана группа камер (не «Все камеры») — показываем кнопку сброса зумов.</summary>
    [ObservableProperty]
    private bool _isGroupSelected;

    /// <summary>Масштаб размера плиток камер (0.5-2.0, по умолчанию 1.0).</summary>
    [ObservableProperty]
    private double _tileScale = 1.0;

    /// <summary>Боковая панель с группами камер видима.</summary>
    [ObservableProperty]
    private bool _isSidebarVisible = true;

    /// <summary>Использование CPU в процентах.</summary>
    [ObservableProperty]
    private double _cpuUsage;

    /// <summary>Использование RAM в процентах.</summary>
    [ObservableProperty]
    private double _ramUsage;

    /// <summary>Использование GPU в процентах.</summary>
    [ObservableProperty]
    private double _gpuUsage;

    /// <summary>Показывать под каждой камерой статистику (поток, задержка, трафик).</summary>
    [ObservableProperty]
    private bool _isStatsVisible;

    [RelayCommand]
    private void ToggleStats()
    {
        IsStatsVisible = !IsStatsVisible;
    }

    private readonly FrigateApiService _api;
    private readonly Action _onLogout;
    private readonly Action<string, FrigateApiService>? _onOpenPlayer;
    private readonly Action? _onOpenSettings;
    private readonly Action? _onOpenEvents;
    private readonly Action? _onOpenRecordings;
    private readonly UserPreferencesService _prefs;
    private readonly CameraSnapshotCache? _snapshotCache;
    private readonly SystemMonitorService _systemMonitor;

    private List<string> _allCameraNames = new();
    private Dictionary<string, CameraGroupConfig> _cameraGroups = new();
    private Dictionary<string, CameraConfig> _cameraConfigs = new();
    private string? _selectedGroupId;

    public CamerasViewModel(
        FrigateApiService api,
        Action onLogout,
        Action<string, FrigateApiService>? onOpenPlayer,
        Action? onOpenSettings = null,
        Action? onOpenEvents = null,
        Action? onOpenRecordings = null,
        UserPreferencesService? prefs = null,
        CameraSnapshotCache? snapshotCache = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onLogout = onLogout ?? throw new ArgumentNullException(nameof(onLogout));
        _onOpenPlayer = onOpenPlayer;
        _onOpenSettings = onOpenSettings;
        _onOpenEvents = onOpenEvents;
        _onOpenRecordings = onOpenRecordings;
        _prefs = prefs ?? new UserPreferencesService();
        _snapshotCache = snapshotCache;
        
        _systemMonitor = new SystemMonitorService();
        _systemMonitor.MetricsUpdated += OnSystemMetricsUpdated;
        _systemMonitor.Start(TimeSpan.FromSeconds(2)); // Обновление каждые 2 секунды
    }

    private void OnSystemMetricsUpdated(double cpu, double ram, double gpu)
    {
        // Обновляем на UI потоке
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            System.Diagnostics.Debug.WriteLine($"CamerasVM: Updating metrics - CPU={cpu:F1}%, RAM={ram:F1}%, GPU={gpu:F1}%");
            CpuUsage = cpu;
            RamUsage = ram;
            GpuUsage = gpu;
        });
    }

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarVisible = !IsSidebarVisible;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        _onOpenSettings?.Invoke();
    }

    [RelayCommand]
    private void OpenEvents()
    {
        _onOpenEvents?.Invoke();
    }

    [RelayCommand]
    private void OpenRecordings()
    {
        _onOpenRecordings?.Invoke();
    }

    private void SaveCameraZoom(CameraItemViewModel vm)
    {
        if (string.IsNullOrEmpty(_selectedGroupId)) return;
        var profileKey = UserPreferencesService.GetProfileKey(_api.BaseUrl, UserName);
        _prefs.SaveCameraZoom(profileKey, _selectedGroupId, vm.Name, new CameraZoomState
        {
            ZoomLevel = vm.ZoomLevel,
            PanX = vm.PanX,
            PanY = vm.PanY
        });
    }

    [RelayCommand]
    private void ResetAllZooms()
    {
        if (string.IsNullOrEmpty(_selectedGroupId)) return;
        foreach (var c in Cameras)
        {
            c.ZoomLevel = 1;
            c.PanX = 0;
            c.PanY = 0;
            SaveCameraZoom(c);
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _systemMonitor?.Stop();
        foreach (var c in Cameras)
            c.Dispose();
        Cameras.Clear();
        _onLogout();
    }

    public void Cleanup()
    {
        _systemMonitor?.Dispose();
        foreach (var c in Cameras)
            c.Dispose();
        Cameras.Clear();
    }

    [RelayCommand]
    private async Task LoadCamerasAsync()
    {
        IsLoading = true;
        LoadError = "";
        try
        {
            var profile = await _api.GetProfileAsync().ConfigureAwait(true);
            UserName = profile.Username ?? "";

            // Загрузить сохраненный масштаб плиток
            var profileKey = UserPreferencesService.GetProfileKey(_api.BaseUrl, UserName);
            TileScale = _prefs.GetTileScale(profileKey);

            var config = await _api.GetConfigAsync().ConfigureAwait(true);
            _allCameraNames = (await _api.GetCameraNamesAsync().ConfigureAwait(true)).ToList();

            _cameraGroups = config.CameraGroups ?? new Dictionary<string, CameraGroupConfig>();
            _cameraConfigs = config.Cameras ?? new Dictionary<string, CameraConfig>();

            // Боковая панель: "Все камеры" + группы (сортировка по order, затем по ключу)
            var sidebar = new List<SidebarItemViewModel>();
            var allItem = new SidebarItemViewModel { Id = null, DisplayName = "Все камеры", IsSelected = true };
            allItem.SelectCommand = new AsyncRelayCommand(async () => await SelectSidebarAsync(null).ConfigureAwait(true));
            sidebar.Add(allItem);
            foreach (var kv in _cameraGroups.OrderBy(x => x.Value.Order).ThenBy(x => x.Key))
            {
                var key = kv.Key;
                var groupItem = new SidebarItemViewModel { Id = key, DisplayName = key, IsSelected = false };
                groupItem.SelectCommand = new AsyncRelayCommand(async () => await SelectSidebarAsync(key).ConfigureAwait(true));
                sidebar.Add(groupItem);
            }

            SidebarItems.Clear();
            foreach (var item in sidebar)
                SidebarItems.Add(item);

            await RefreshCameraListAsync(null).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectSidebarAsync(string? id)
    {
        foreach (var item in SidebarItems)
            item.IsSelected = item.Id == id;
        await RefreshCameraListAsync(id).ConfigureAwait(true);
    }

    private async Task RefreshCameraListAsync(string? groupId)
    {
        _selectedGroupId = groupId;
        IsGroupSelected = !string.IsNullOrEmpty(groupId);

        var names = groupId == null || !_cameraGroups.TryGetValue(groupId, out var group)
            ? _allCameraNames
            : _allCameraNames.Intersect(group.Cameras ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();

        Title = groupId == null ? "Все камеры" : groupId;
        if (names.Count > 0)
            Title += $" ({names.Count})";

        foreach (var c in Cameras)
            c.Dispose();
        Cameras.Clear();

        var profileKey = UserPreferencesService.GetProfileKey(_api.BaseUrl, UserName);
        var zooms = _prefs.GetCameraZooms(profileKey, groupId);
        var isZoomEnabled = !string.IsNullOrEmpty(groupId);

        foreach (var name in names)
        {
            var initialZoom = zooms.TryGetValue(name, out var z) ? z : null;
            var rotation = _cameraConfigs.TryGetValue(name, out var camConfig) ? camConfig.Rotate : 0f;
            var item = new CameraItemViewModel(
                name,
                _api,
                () => _onOpenPlayer?.Invoke(name, _api),
                initialZoom,
                SaveCameraZoom,
                isZoomEnabled,
                _snapshotCache,
                rotation,
                TileScale);
            Cameras.Add(item);
            item.StartRefresh();
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    partial void OnTileScaleChanged(double value)
    {
        // Сохранить новый масштаб
        var profileKey = UserPreferencesService.GetProfileKey(_api.BaseUrl, UserName);
        _prefs.SaveTileScale(profileKey, value);

        // Обновить все существующие камеры
        foreach (var camera in Cameras)
        {
            camera.UpdateTileScale(value);
        }
    }
}

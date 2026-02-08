using System;
using CommunityToolkit.Mvvm.ComponentModel;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    private FrigateApiService? _camerasApi;
    /// <summary>Ссылка на экран камер — при возврате из проигрывателя восстанавливаем выбранную группу.</summary>
    private CamerasViewModel? _camerasViewModel;
    private EventsViewModel? _eventsViewModel;
    private ViewModelBase? _viewModelBeforeClip;
    private readonly UserPreferencesService _prefs = new();
    /// <summary>Кэш превью камер — сохраняет картинку при переходах между группами.</summary>
    private readonly CameraSnapshotCache _snapshotCache = new();

    public MainWindowViewModel()
    {
        NavigateToLogin();
    }

    internal void NavigateToCameras(FrigateApiService api)
    {
        _camerasApi = api;
        _camerasViewModel = new CamerasViewModel(api, NavigateToLogin, NavigateToPlayer, NavigateToSettings, NavigateToEvents, NavigateToRecordings, _prefs, _snapshotCache);
        CurrentViewModel = _camerasViewModel;
        _ = _camerasViewModel.LoadCamerasCommand.ExecuteAsync(null);
    }

    internal void NavigateToSettings()
    {
        CurrentViewModel = new SettingsViewModel(NavigateBackToCameras, _prefs);
    }

    internal void NavigateToPlayer(string cameraName, FrigateApiService api)
    {
        var vm = new CameraPlayerViewModel(cameraName, api, NavigateBackToCameras);
        CurrentViewModel = vm;
        _ = vm.StartAsync(); // Загрузить RTSP URL и начать воспроизведение
    }

    internal void NavigateToEvents()
    {
        if (_camerasApi == null) return;
        _eventsViewModel = new EventsViewModel(_camerasApi, NavigateBackToCameras, NavigateToEventClip);
        CurrentViewModel = _eventsViewModel;
        _ = _eventsViewModel.LoadEventsCommand.ExecuteAsync(null);
    }

    /// <summary>По клику на событие: если есть клип — сразу открыть плеер, иначе — экран детали.</summary>
    internal void NavigateToEventClip(string eventId)
    {
        _ = NavigateToEventClipAsync(eventId);
    }

    private async System.Threading.Tasks.Task NavigateToEventClipAsync(string eventId)
    {
        if (_camerasApi == null) return;
        try
        {
            var ev = await _camerasApi.GetEventByIdAsync(eventId).ConfigureAwait(true);
            if (ev?.HasClip == true)
            {
                var url = _camerasApi.GetEventClipUrl(ev.Id);
                var title = $"{ev.Label} — {ev.Camera}";
                NavigateToClipPlayer(url, title);
            }
            else
                NavigateToEventDetail(eventId);
        }
        catch
        {
            NavigateToEventDetail(eventId);
        }
    }

    internal void NavigateToEventDetail(string eventId)
    {
        if (_camerasApi == null) return;
        CurrentViewModel = new EventDetailViewModel(_camerasApi, eventId, NavigateBackToEvents, NavigateToClipPlayer);
        _ = ((EventDetailViewModel)CurrentViewModel).LoadEventCommand.ExecuteAsync(null);
    }

    private void NavigateBackToEvents()
    {
        if (_eventsViewModel != null)
            CurrentViewModel = _eventsViewModel;
        else
            NavigateBackToCameras();
    }

    internal void NavigateToRecordings()
    {
        if (_camerasApi == null) return;
        CurrentViewModel = new RecordingsViewModel(_camerasApi, NavigateBackToCameras, NavigateToClipPlayer);
        _ = ((RecordingsViewModel)CurrentViewModel).LoadCamerasAndSummaryCommand.ExecuteAsync(null);
    }

    internal void NavigateToClipPlayer(string mediaUrl, string title)
    {
        _viewModelBeforeClip = CurrentViewModel;
        CurrentViewModel = new ClipPlayerViewModel(mediaUrl, title, NavigateBackFromClipPlayer);
    }

    private void NavigateBackFromClipPlayer()
    {
        if (_viewModelBeforeClip != null)
            CurrentViewModel = _viewModelBeforeClip;
        else
            NavigateBackToCameras();
    }

    private void NavigateBackToCameras()
    {
        // Возвращаемся в тот же экран камер с сохранённой выбранной группой (не «Все камеры»)
        if (_camerasViewModel != null)
        {
            CurrentViewModel = _camerasViewModel;
            return;
        }
        if (_camerasApi == null) return;
        NavigateToCameras(_camerasApi);
    }

    internal void NavigateToLogin()
    {
        _camerasApi = null;
        _camerasViewModel = null;
        _snapshotCache.Clear(); // Очистить кэш превью при выходе
        CurrentViewModel = new LoginViewModel(NavigateToCameras, _prefs);
    }
}

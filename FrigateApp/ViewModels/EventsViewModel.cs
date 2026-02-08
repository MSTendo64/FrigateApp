using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Controls;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class EventsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<EventItemViewModel> _events = new();

    [ObservableProperty]
    private EventItemViewModel? _selectedEventItem;

    [ObservableProperty]
    private double _timelineRangeStart;

    [ObservableProperty]
    private double _timelineRangeEnd;

    [ObservableProperty]
    private ObservableCollection<TimelineSegmentItem> _timelineSegments = new();

    /// <summary>Ширина таймлайна в пикселях (для горизонтальной прокрутки по времени).</summary>
    [ObservableProperty]
    private double _timelineWidth = 400;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _selectedCamera = "all";

    [ObservableProperty]
    private string _selectedLabel = "all";

    [ObservableProperty]
    private string _limitText = "100";

    [ObservableProperty]
    private ObservableCollection<string> _cameraNames = new();

    private readonly FrigateApiService _api;
    private readonly Action _onBack;
    private readonly Action<string> _onOpenEvent;

    public EventsViewModel(FrigateApiService api, Action onBack, Action<string>? onOpenEvent = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
        _onOpenEvent = onOpenEvent ?? (_ => { });
    }

    /// <summary>Вызывается при клике по таймлайну — скроллит список к событию в это время.</summary>
    public void OnTimelineClicked(double unixTime)
    {
        EventItemViewModel? best = null;
        foreach (var ev in Events)
        {
            var end = ev.EndTime ?? ev.StartTime + 60;
            if (unixTime >= ev.StartTime && unixTime <= end)
            {
                best = ev;
                break;
            }
        }
        if (best == null)
            best = Events.OrderBy(e => Math.Abs(e.StartTime - unixTime)).FirstOrDefault();
        if (best != null)
            SelectedEventItem = best;
    }


    [RelayCommand]
    private void Back()
    {
        _onBack();
    }

    [RelayCommand]
    private async Task LoadEventsAsync()
    {
        if (CameraNames.Count == 0)
            await LoadCamerasAsync().ConfigureAwait(true);

        IsLoading = true;
        ErrorText = "";
        try
        {
            if (!int.TryParse(LimitText, out var limit) || limit < 1) limit = 100;
            var list = await _api.GetEventsAsync(
                cameras: SelectedCamera,
                labels: SelectedLabel,
                limit: limit,
                sort: "date_desc").ConfigureAwait(true);

            Events.Clear();
            foreach (var dto in list)
            {
                var item = new EventItemViewModel(dto, _onOpenEvent);
                Events.Add(item);
                _ = LoadThumbnailAsync(item);
            }

            // Таймлайн: один день от минимума до максимума времени событий
            var segments = new ObservableCollection<TimelineSegmentItem>();
            if (list.Count > 0)
            {
                var minT = list.Min(e => e.StartTime);
                var maxT = list.Max(e => e.EndTime ?? e.StartTime + 60);
                var day = 86400.0;
                TimelineRangeStart = Math.Floor(minT / day) * day;
                TimelineRangeEnd = TimelineRangeStart + day;
                if (maxT > TimelineRangeEnd)
                    TimelineRangeEnd = Math.Ceiling((maxT - TimelineRangeStart) / day) * day + TimelineRangeStart;
                // Ширина таймлайна ~80 px в час для прокрутки по времени
                TimelineWidth = Math.Max(400, (TimelineRangeEnd - TimelineRangeStart) / 3600.0 * 80);
                foreach (var dto in list)
                {
                    var end = dto.EndTime ?? dto.StartTime + 60;
                    segments.Add(new TimelineSegmentItem
                    {
                        StartUnix = dto.StartTime,
                        EndUnix = end
                    });
                }
            }
            TimelineSegments = segments;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadThumbnailAsync(EventItemViewModel item)
    {
        try
        {
            var bytes = await _api.GetEventThumbnailBytesAsync(item.Id).ConfigureAwait(true);
            if (bytes == null || bytes.Length == 0) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    using var ms = new System.IO.MemoryStream(bytes);
                    item.Thumbnail = new Avalonia.Media.Imaging.Bitmap(ms);
                }
                catch { /* ignore */ }
            });
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private async Task LoadCamerasAsync()
    {
        try
        {
            var names = await _api.GetCameraNamesAsync().ConfigureAwait(true);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CameraNames.Clear();
                CameraNames.Add("all");
                foreach (var n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    CameraNames.Add(n);
            });
        }
        catch { /* ignore */ }
    }
}

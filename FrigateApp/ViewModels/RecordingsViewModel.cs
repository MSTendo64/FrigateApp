using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Controls;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class RecordingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<string> _cameraNames = new();

    [ObservableProperty]
    private ObservableCollection<CameraRecordingItemViewModel> _cameraItems = new();

    [ObservableProperty]
    private string? _expandedCameraName;

    /// <summary>URL HLS раскрытой камеры (для одного плеера в UI).</summary>
    [ObservableProperty]
    private string _expandedCameraVodUrl = "";

    /// <summary>Позиция для seek раскрытого плеера (мс от начала дня).</summary>
    [ObservableProperty]
    private long _expandedCameraSeekMs;

    [ObservableProperty]
    private ObservableCollection<string> _daysWithRecordings = new();

    [ObservableProperty]
    private string _selectedDay = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private double _timelineRangeStart;

    [ObservableProperty]
    private double _timelineRangeEnd;

    [ObservableProperty]
    private double _timelineWidth = 400;

    [ObservableProperty]
    private ObservableCollection<TimelineSegmentItem> _timelineSegments = new();

    [ObservableProperty]
    private double _selectedTime;

    [ObservableProperty]
    private bool _isLoadingPreviews;

    private readonly FrigateApiService _api;
    private readonly Action _onBack;
    private readonly Action<string, string>? _onPlayInApp;
    private CancellationTokenSource? _previewCts;

    public RecordingsViewModel(FrigateApiService api, Action onBack, Action<string, string>? onPlayInApp = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
        _onPlayInApp = onPlayInApp;
    }

    [RelayCommand]
    private void Back()
    {
        _onBack();
    }

    [RelayCommand]
    private async Task LoadCamerasAndSummaryAsync()
    {
        IsLoading = true;
        ErrorText = "";
        try
        {
            var names = (await _api.GetCameraNamesAsync().ConfigureAwait(true)).ToList();
            CameraNames.Clear();
            foreach (var n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                CameraNames.Add(n);

            var daysSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cam in CameraNames)
            {
                try
                {
                    var list = await _api.GetCameraRecordingsSummaryAsync(cam).ConfigureAwait(true);
                    if (list != null)
                        foreach (var d in list)
                            daysSet.Add(d.Day);
                }
                catch { /* ignore per-camera */ }
            }
            DaysWithRecordings.Clear();
            foreach (var d in daysSet.OrderByDescending(x => x))
                DaysWithRecordings.Add(d);
            if (DaysWithRecordings.Count > 0 && string.IsNullOrEmpty(SelectedDay))
                SelectedDay = DaysWithRecordings[0];

            BuildCameraItems();
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

    private void BuildCameraItems()
    {
        CameraItems.Clear();
        foreach (var name in CameraNames)
        {
            var item = new CameraRecordingItemViewModel(name, ToggleExpandCamera);
            CameraItems.Add(item);
        }
    }

    private void ToggleExpandCamera(string cameraName)
    {
        if (string.IsNullOrEmpty(cameraName)) return;
        ExpandedCameraName = ExpandedCameraName == cameraName ? null : cameraName;
        foreach (var item in CameraItems)
            item.IsExpanded = item.CameraName == ExpandedCameraName;
        UpdateExpandedPlayerUrlAndSeek();
    }

    partial void OnExpandedCameraNameChanged(string? value)
    {
        foreach (var item in CameraItems)
            item.IsExpanded = item.CameraName == value;
        UpdateExpandedPlayerUrlAndSeek();
    }

    private void UpdateExpandedPlayerUrlAndSeek()
    {
        if (string.IsNullOrEmpty(ExpandedCameraName))
        {
            ExpandedCameraVodUrl = "";
            ExpandedCameraSeekMs = 0;
            return;
        }
        var item = CameraItems.FirstOrDefault(c => c.CameraName == ExpandedCameraName);
        if (item != null)
        {
            ExpandedCameraVodUrl = item.VodPlaylistUrl ?? "";
            ExpandedCameraSeekMs = item.SeekOffsetMs;
        }
    }

    partial void OnSelectedDayChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        _ = LoadSegmentsAndPreviewsForDayAsync().ConfigureAwait(false);
    }

    partial void OnSelectedTimeChanged(double value)
    {
        var (start, _) = DayToUnixRange(SelectedDay);
        var seekMs = (long)((value - start) * 1000);
        foreach (var item in CameraItems)
            item.SeekOffsetMs = seekMs;
        if (!string.IsNullOrEmpty(ExpandedCameraName))
            ExpandedCameraSeekMs = seekMs;
        _ = LoadAllPreviewsAsync().ConfigureAwait(false);
    }

    public void OnTimelineClicked(double unixTime)
    {
        var (start, end) = DayToUnixRange(SelectedDay);
        SelectedTime = Math.Clamp(unixTime, start, end);
    }

    private async Task LoadSegmentsAndPreviewsForDayAsync()
    {
        if (string.IsNullOrEmpty(SelectedDay) || CameraItems.Count == 0) return;
        IsLoading = true;
        ErrorText = "";
        try
        {
            var (start, end) = DayToUnixRange(SelectedDay);
            var allSegments = new List<TimelineSegmentItem>();
            var tasks = CameraItems.Select(async item =>
            {
                var list = await _api.GetCameraRecordingsAsync(item.CameraName, start, end).ConfigureAwait(true);
                item.VodPlaylistUrl = _api.GetVodPlaylistUrl(item.CameraName, start, end) + "/master.m3u8";
                return (item.CameraName, Segments: list ?? new List<RecordingSegmentDto>());
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(true);

            foreach (var (cameraName, segments) in results)
            {
                foreach (var s in segments)
                    allSegments.Add(new TimelineSegmentItem { StartUnix = s.StartTime, EndUnix = s.EndTime });
            }

            TimelineRangeStart = start;
            TimelineRangeEnd = end;
            TimelineWidth = Math.Max(400, 24 * 80);
            TimelineSegments = new ObservableCollection<TimelineSegmentItem>(
                allSegments.OrderBy(x => x.StartUnix).ToList());

            var lastEnd = allSegments.Count > 0 ? allSegments.Max(x => x.EndUnix) : end;
            SelectedTime = lastEnd;

            foreach (var item in CameraItems)
            {
                var offsetSec = SelectedTime - start;
                item.SeekOffsetMs = (long)(offsetSec * 1000);
            }

            await LoadAllPreviewsAsync().ConfigureAwait(false);
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

    private async Task LoadAllPreviewsAsync()
    {
        if (string.IsNullOrEmpty(SelectedDay)) return;
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var cts = _previewCts;
        IsLoadingPreviews = true;
        try
        {
            var (start, end) = DayToUnixRange(SelectedDay);
            var time = Math.Clamp(SelectedTime, start, end);
            var tasks = CameraItems.Select(async item =>
            {
                try
                {
                    var bytes = await _api.GetRecordingSnapshotBytesAsync(item.CameraName, time, cts.Token).ConfigureAwait(false);
                    return (item.CameraName, Bytes: bytes);
                }
                catch (OperationCanceledException)
                {
                    return (item.CameraName, Bytes: (byte[]?)null);
                }
            }).ToList();
            var results = await Task.WhenAll(tasks).ConfigureAwait(true);
            if (cts.Token.IsCancellationRequested) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (cts.Token.IsCancellationRequested) return;
                foreach (var (cameraName, bytes) in results)
                {
                    var item = CameraItems.FirstOrDefault(c => c.CameraName == cameraName);
                    if (item == null) continue;
                    if (bytes == null || bytes.Length == 0)
                    {
                        item.PreviewImage = null;
                        continue;
                    }
                    try
                    {
                        using var ms = new System.IO.MemoryStream(bytes);
                        item.PreviewImage = new Bitmap(ms);
                    }
                    catch { item.PreviewImage = null; }
                }
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally
        {
            if (cts == _previewCts)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => IsLoadingPreviews = false);
        }
    }

    private static (double start, double end) DayToUnixRange(string day)
    {
        if (string.IsNullOrEmpty(day) || day.Length < 10) return (0, 0);
        if (DateTime.TryParse(day, out var dt))
        {
            var start = new DateTimeOffset(dt.Year, dt.Month, dt.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            var end = start + 24 * 3600 - 1;
            return (start, end);
        }
        return (0, 0);
    }

    [RelayCommand]
    private void OpenVodInPlayer(string? cameraName)
    {
        var item = string.IsNullOrEmpty(cameraName) ? null : CameraItems.FirstOrDefault(c => c.CameraName == cameraName);
        var url = item?.VodPlaylistUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand]
    private void PlayInApp(string? cameraName)
    {
        var item = string.IsNullOrEmpty(cameraName) ? null : CameraItems.FirstOrDefault(c => c.CameraName == cameraName);
        var url = item?.VodPlaylistUrl;
        if (string.IsNullOrEmpty(url)) return;
        _onPlayInApp?.Invoke(url, $"Запись {cameraName} {SelectedDay}");
    }
}

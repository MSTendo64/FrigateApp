using System;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public class RecordingSegmentItemViewModel : ViewModelBase
{
    public RecordingSegmentDto Segment { get; }
    public string CameraName { get; }
    private readonly FrigateApiService _api;

    public string TimeRangeStr => $"{StartTime:HH:mm:ss} — {EndTime:HH:mm:ss}";
    public DateTime StartTime => DateTimeOffset.FromUnixTimeSeconds((long)Segment.StartTime).LocalDateTime;
    public DateTime EndTime => DateTimeOffset.FromUnixTimeSeconds((long)Segment.EndTime).LocalDateTime;
    public double Duration => Segment.Duration;
    public string DurationStr => TimeSpan.FromSeconds(Duration).ToString(@"mm\:ss");

    public RecordingSegmentItemViewModel(RecordingSegmentDto segment, string cameraName, FrigateApiService api)
    {
        Segment = segment ?? throw new ArgumentNullException(nameof(segment));
        CameraName = cameraName ?? throw new ArgumentNullException(nameof(cameraName));
        _api = api ?? throw new ArgumentNullException(nameof(api));
    }

    public string GetVodUrl() => _api.GetVodPlaylistUrl(CameraName, Segment.StartTime, Segment.EndTime);
}

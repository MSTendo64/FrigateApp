using System;
using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;

namespace FrigateApp.ViewModels;

public partial class EventItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _camera = "";

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string? _subLabel;

    [ObservableProperty]
    private string _startTimeText = "";

    [ObservableProperty]
    private string _durationText = "";

    [ObservableProperty]
    private Bitmap? _thumbnail;

    [ObservableProperty]
    private bool _hasClip;

    [ObservableProperty]
    private bool _hasSnapshot;

    public double StartTime { get; private set; }
    public double? EndTime { get; private set; }

    private readonly Action<string> _onOpen;

    public EventItemViewModel(EventDto dto, Action<string> onOpen)
    {
        _onOpen = onOpen;
        UpdateFromDto(dto);
    }

    public void UpdateFromDto(EventDto dto)
    {
        Id = dto.Id;
        Camera = dto.Camera;
        Label = dto.Label;
        SubLabel = string.IsNullOrEmpty(dto.SubLabel) ? null : dto.SubLabel;
        StartTime = dto.StartTime;
        EndTime = dto.EndTime;
        HasClip = dto.HasClip;
        HasSnapshot = dto.HasSnapshot;

        var startDt = DateTimeOffset.FromUnixTimeSeconds((long)dto.StartTime).LocalDateTime;
        StartTimeText = startDt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentUICulture);

        if (dto.EndTime.HasValue)
        {
            var dur = dto.EndTime.Value - dto.StartTime;
            DurationText = dur < 60 ? $"{(int)dur} с" : $"{(int)(dur / 60)} мин";
        }
        else
            DurationText = "…";
    }

    [RelayCommand]
    private void Open()
    {
        _onOpen(Id);
    }
}

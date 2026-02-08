using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class EventDetailViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _eventId = "";

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
    private Bitmap? _snapshot;

    [ObservableProperty]
    private bool _hasClip;

    [ObservableProperty]
    private string _clipUrl = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private bool _isContentVisible;

    [ObservableProperty]
    private string _errorText = "";

    partial void OnIsLoadingChanged(bool value) => IsContentVisible = !value;

    private readonly FrigateApiService _api;
    private readonly Action _onBack;
    private readonly Action<string, string>? _onPlayClipInApp;

    public EventDetailViewModel(FrigateApiService api, string eventId, Action onBack, Action<string, string>? onPlayClipInApp = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
        _onPlayClipInApp = onPlayClipInApp;
        _eventId = eventId ?? "";
    }

    [RelayCommand]
    private void Back()
    {
        _onBack();
    }

    [RelayCommand]
    private async Task LoadEventAsync()
    {
        IsLoading = true;
        ErrorText = "";
        try
        {
            var ev = await _api.GetEventByIdAsync(EventId).ConfigureAwait(true);
            if (ev == null)
            {
                ErrorText = "Событие не найдено.";
                return;
            }

            Camera = ev.Camera;
            Label = ev.Label;
            SubLabel = string.IsNullOrEmpty(ev.SubLabel) ? null : ev.SubLabel;
            HasClip = ev.HasClip;

            var startDt = DateTimeOffset.FromUnixTimeSeconds((long)ev.StartTime).LocalDateTime;
            StartTimeText = startDt.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.CurrentUICulture);

            if (ev.EndTime.HasValue)
            {
                var dur = ev.EndTime.Value - ev.StartTime;
                DurationText = dur < 60 ? $"{(int)dur} с" : $"{(int)(dur / 60)} мин";
            }
            else
                DurationText = "…";

            if (ev.HasClip)
                ClipUrl = _api.GetEventClipUrl(ev.Id);

            var bytes = await _api.GetEventThumbnailBytesAsync(ev.Id).ConfigureAwait(true);
            if (bytes != null && bytes.Length > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        using var ms = new System.IO.MemoryStream(bytes);
                        Snapshot = new Bitmap(ms);
                    }
                    catch { /* ignore */ }
                });
            }
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

    [RelayCommand]
    private void OpenClipInApp()
    {
        if (string.IsNullOrEmpty(ClipUrl)) return;
        _onPlayClipInApp?.Invoke(ClipUrl, $"{Label} — {Camera}");
    }
}

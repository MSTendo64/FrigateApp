using System;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FrigateApp.ViewModels;

/// <summary>Одна камера в сетке записей: превью по времени таймлайна, по клику раскрывается в плеер.</summary>
public partial class CameraRecordingItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _cameraName = "";

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private string _vodPlaylistUrl = "";

    /// <summary>Смещение в мс от начала дня для первоначального seek плеера.</summary>
    public long SeekOffsetMs { get; set; }

    private readonly Action<string> _onToggleExpand;

    public CameraRecordingItemViewModel(string cameraName, Action<string> onToggleExpand)
    {
        _cameraName = cameraName ?? "";
        _onToggleExpand = onToggleExpand ?? (_ => { });
    }

    [RelayCommand]
    private void ToggleExpand()
    {
        _onToggleExpand(CameraName);
    }
}

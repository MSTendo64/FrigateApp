using System;
using System.ComponentModel;
using Avalonia.Controls;
using FrigateApp.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class RecordingsView : UserControl
{
    private RecordingsViewModel? _vm;

    public RecordingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm = DataContext as RecordingsViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            UpdateExpandedPlayer();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordingsViewModel.ExpandedCameraSeekMs))
        {
            SeekExpandedPlayer();
            return;
        }
        if (e.PropertyName is nameof(RecordingsViewModel.ExpandedCameraVodUrl) or nameof(RecordingsViewModel.ExpandedCameraName))
            UpdateExpandedPlayer();
    }

    private void UpdateExpandedPlayer()
    {
        var player = this.FindControl<CameraPlayer>("ExpandedPlayer");
        if (player == null || _vm == null) return;
        if (string.IsNullOrEmpty(_vm.ExpandedCameraName) || string.IsNullOrEmpty(_vm.ExpandedCameraVodUrl))
        {
            player.Stop();
            return;
        }
        player.PlayMediaUrl(_vm.ExpandedCameraVodUrl, 0f);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try { player.SetTimeMs(_vm.ExpandedCameraSeekMs); }
            catch { /* ignore */ }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void SeekExpandedPlayer()
    {
        var player = this.FindControl<CameraPlayer>("ExpandedPlayer");
        if (player == null || _vm == null || string.IsNullOrEmpty(_vm.ExpandedCameraName)) return;
        try
        {
            player.SetTimeMs(_vm.ExpandedCameraSeekMs);
        }
        catch { /* ignore */ }
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var timeline = this.FindControl<TimelineControl>("Timeline");
        if (timeline != null && DataContext is RecordingsViewModel vm)
        {
            timeline.TimeClicked -= OnTimelineTimeClicked;
            timeline.TimeClicked += OnTimelineTimeClicked;
        }
    }

    private void OnTimelineTimeClicked(double unixTime)
    {
        if (DataContext is RecordingsViewModel vm)
            vm.OnTimelineClicked(unixTime);
    }
}

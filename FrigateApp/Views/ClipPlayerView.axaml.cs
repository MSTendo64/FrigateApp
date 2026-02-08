using System;
using Avalonia.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class ClipPlayerView : UserControl
{
    private Controls.CameraPlayer? _player;
    private Button? _btnPlayPause;
    private ComboBox? _speedCombo;

    public ClipPlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _player = this.FindControl<Controls.CameraPlayer>("Player");
        _btnPlayPause = this.FindControl<Button>("BtnPlayPause");
        var btnBack10 = this.FindControl<Button>("BtnBack10");
        var btnFwd10 = this.FindControl<Button>("BtnFwd10");
        _speedCombo = this.FindControl<ComboBox>("SpeedCombo");

        if (_player != null)
        {
            _player.IsPlayingChanged += UpdatePlayPauseButton;
            if (_btnPlayPause != null)
                _btnPlayPause.Click += (_, _) => _player.TogglePause();
        }
        if (btnBack10 != null && _player != null)
            btnBack10.Click += (_, _) => _player.SeekRelative(-10);
        if (btnFwd10 != null && _player != null)
            btnFwd10.Click += (_, _) => _player.SeekRelative(10);
        if (_speedCombo != null && _player != null)
        {
            _speedCombo.SelectionChanged += (_, _) =>
            {
                var idx = _speedCombo.SelectedIndex;
                var rate = idx switch { 0 => 0.5f, 1 => 1f, 2 => 1.5f, 3 => 2f, _ => 1f };
                _player.SetRate(rate);
            };
        }
        UpdatePlayPauseButton();
    }

    private void UpdatePlayPauseButton()
    {
        if (_btnPlayPause == null || _player == null) return;
        _btnPlayPause.Content = _player.IsPlaying ? "⏸" : "▶";
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ClipPlayerViewModel vm || string.IsNullOrEmpty(vm.MediaUrl)) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var player = this.FindControl<Controls.CameraPlayer>("Player");
                if (player != null)
                {
                    player.PlayMediaUrl(vm.MediaUrl, 0f);
                    _player = player;
                    UpdatePlayPauseButton();
                }
            }
            catch (Exception ex)
            {
                vm.ShowFallbackMessage = true;
                vm.ErrorText = ex.Message;
            }
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_player != null)
        {
            _player.IsPlayingChanged -= UpdatePlayPauseButton;
            _player.Stop();
        }
        _player = null;
        _btnPlayPause = null;
        _speedCombo = null;
        base.OnDetachedFromVisualTree(e);
    }
}

using System;
using Avalonia;
using Avalonia.Controls;
using LibVLCSharp.Shared;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class LowLatencyCameraPlayerView : UserControl
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private System.ComponentModel.PropertyChangedEventHandler? _propertyChangedHandler;

    public LowLatencyCameraPlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        // Инициализация LibVLC
        Core.Initialize();
        _libVlc = new LibVLC("--no-audio", "--network-caching=100", "--demux=mp4", ":http-reconnect");
        _mediaPlayer = new MediaPlayer(_libVlc);
        VideoView.MediaPlayer = _mediaPlayer;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        // Отписываемся от старого ViewModel
        if (_propertyChangedHandler != null && DataContext is LowLatencyCameraPlayerViewModel oldVm)
        {
            oldVm.PropertyChanged -= _propertyChangedHandler;
        }

        if (DataContext is LowLatencyCameraPlayerViewModel vm)
        {
            _propertyChangedHandler = (s, args) =>
            {
                if (args.PropertyName == nameof(LowLatencyCameraPlayerViewModel.VideoUrl) &&
                    !string.IsNullOrEmpty(vm.VideoUrl))
                {
                    PlayStream(vm.VideoUrl);
                }
            };
            
            vm.PropertyChanged += _propertyChangedHandler;

            if (!string.IsNullOrEmpty(vm.VideoUrl))
                PlayStream(vm.VideoUrl);
        }
    }

    private void PlayStream(string url)
    {
        if (_mediaPlayer == null || string.IsNullOrEmpty(url)) return;

        try
        {
            Console.WriteLine($"[VideoView] Playing: {url}");

            _mediaPlayer.Stop();
            _currentMedia?.Dispose();

            // HTTP URL - используем FromLocation
            _currentMedia = new Media(_libVlc!, url, FromType.FromLocation);
            _currentMedia.AddOption(":demux=mp4");
            _currentMedia.AddOption(":network-caching=500"); // 500ms кэш для сети

            _mediaPlayer.Play(_currentMedia);

            Console.WriteLine($"[VideoView] Playback started");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoView] Error: {ex.Message}");
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        try
        {
            // Сначала отключаем MediaPlayer от VideoView
            if (VideoView != null && VideoView.MediaPlayer != null)
            {
                VideoView.MediaPlayer = null;
            }
            
            // Потом останавливаем и утилизируем
            _mediaPlayer?.Stop();
            _currentMedia?.Dispose();
            _currentMedia = null;
            
            _mediaPlayer?.Dispose();
            _mediaPlayer = null;
            
            _libVlc?.Dispose();
            _libVlc = null;
            
            // Отписываемся от событий
            if (DataContext is LowLatencyCameraPlayerViewModel vm && _propertyChangedHandler != null)
            {
                vm.PropertyChanged -= _propertyChangedHandler;
                _propertyChangedHandler = null;
            }
        }
        catch { }
    }
    
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        
        // Пересоздаём LibVLC при повторном прикреплении
        if (_libVlc == null)
        {
            Core.Initialize();
            _libVlc = new LibVLC("--no-audio", "--network-caching=100", "--demux=mp4", ":http-reconnect");
            _mediaPlayer = new MediaPlayer(_libVlc);
            if (VideoView != null)
                VideoView.MediaPlayer = _mediaPlayer;
        }
    }
}

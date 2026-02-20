using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FrigateApp.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class CameraPlayerView : UserControl
{
    public CameraPlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is CameraPlayerViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(CameraPlayerViewModel.VideoFilePath) &&
                    !string.IsNullOrEmpty(vm.VideoFilePath) &&
                    Player != null)
                {
                    StartPlayback(vm);
                }
            };
            // Если путь уже установлен (например, подписка произошла после FileReady), сразу запускаем
            if (!string.IsNullOrEmpty(vm.VideoFilePath) && Player != null)
                StartPlayback(vm);
        }
    }

    private void StartPlayback(CameraPlayerViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.VideoFilePath) || Player == null) return;
        System.Diagnostics.Debug.WriteLine($"Starting playback from file: {vm.VideoFilePath} with rotation: {vm.Rotation}°");
        
        Player.FirstFrameReceived -= OnPlayerFirstFrame;
        Player.ZoomStateChanged -= OnPlayerZoomStateChanged;
        Player.FirstFrameReceived += OnPlayerFirstFrame;
        Player.ZoomStateChanged += OnPlayerZoomStateChanged;
        
        // Проверяем существование файла
        if (!System.IO.File.Exists(vm.VideoFilePath))
        {
            System.Diagnostics.Debug.WriteLine($"File does not exist: {vm.VideoFilePath}");
            return;
        }
        
        // Проверяем размер файла
        var fileInfo = new System.IO.FileInfo(vm.VideoFilePath);
        System.Diagnostics.Debug.WriteLine($"File size: {fileInfo.Length / 1024} KB");
        
        if (fileInfo.Length < 1024) // Меньше 1 KB
        {
            System.Diagnostics.Debug.WriteLine("File too small, waiting...");
            // Ждём немного и пробуем снова
            _ = System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
            {
                if (!string.IsNullOrEmpty(vm.VideoFilePath) && Player != null)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => StartPlayback(vm));
            });
            return;
        }
        
        Player.PlayMediaUrl(vm.VideoFilePath!, vm.Rotation);
        Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
        
        // Таймаут на первый кадр
        _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(t =>
        {
            if (Player?.IsPlaying == false && !string.IsNullOrEmpty(vm.VideoFilePath))
            {
                System.Diagnostics.Debug.WriteLine("No frames after 5s, restarting playback...");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Player?.Stop();
                    Player?.PlayMediaUrl(vm.VideoFilePath!, vm.Rotation);
                });
            }
        });
    }

    private void OnPlayerFirstFrame()
    {
        System.Diagnostics.Debug.WriteLine("First frame received (LibVLC, fMP4), notifying ViewModel");
        if (DataContext is not CameraPlayerViewModel vm) return;
        vm.OnVideoStarted();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Player != null)
                Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        
        // Очищаем снапшот при выходе — предотвращаем NullReferenceException
        if (DataContext is CameraPlayerViewModel vm)
        {
            try
            {
                vm.Snapshot?.Dispose();
            }
            catch { }
            vm.Snapshot = null;
        }
        
        try
        {
            Player?.Stop();
        }
        catch { }
    }

    private void OnPlayerZoomStateChanged(double zoom, double panX, double panY)
    {
        if (DataContext is CameraPlayerViewModel vm)
        {
            vm.ZoomLevel = zoom;
            vm.PanX = panX;
            vm.PanY = panY;
        }
    }
}

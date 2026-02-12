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
        Player.PlayMediaUrl(vm.VideoFilePath!, vm.Rotation);
        Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
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

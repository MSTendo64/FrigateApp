using Avalonia;
using Avalonia.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class ProfessionalCameraPlayerView : UserControl
{
    public ProfessionalCameraPlayerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ProfessionalCameraPlayerViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(ProfessionalCameraPlayerViewModel.VideoFilePath) &&
                    !string.IsNullOrEmpty(vm.VideoFilePath) &&
                    Player != null)
                {
                    StartPlayback(vm);
                }
            };
            
            if (!string.IsNullOrEmpty(vm.VideoFilePath) && Player != null)
                StartPlayback(vm);
        }
    }

    private void StartPlayback(ProfessionalCameraPlayerViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.VideoFilePath) || Player == null) return;
        
        Player.FirstFrameReceived -= OnPlayerFirstFrame;
        Player.ZoomStateChanged -= OnPlayerZoomStateChanged;
        Player.FirstFrameReceived += OnPlayerFirstFrame;
        Player.ZoomStateChanged += OnPlayerZoomStateChanged;
        Player.PlayMediaUrl(vm.VideoFilePath!, vm.Rotation);
        Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
    }

    private void OnPlayerFirstFrame()
    {
        if (DataContext is not ProfessionalCameraPlayerViewModel vm) return;
        vm.OnVideoStarted();
    }

    private void OnPlayerZoomStateChanged(double zoom, double panX, double panY)
    {
        if (DataContext is ProfessionalCameraPlayerViewModel vm)
        {
            vm.ZoomLevel = zoom;
            vm.PanX = panX;
            vm.PanY = panY;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        try { Player?.Stop(); } catch { }
    }
}

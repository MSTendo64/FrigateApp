using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using FrigateApp.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class CameraPlayerView : UserControl
{
    private bool _mjpegDragging;
    private Point _mjpegDragStart;

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
                if (args.PropertyName == nameof(vm.RtspUrl) && !string.IsNullOrEmpty(vm.RtspUrl))
                {
                    System.Diagnostics.Debug.WriteLine($"Starting playback: {vm.RtspUrl} with rotation: {vm.Rotation}° (UseRtspSharp={vm.UseRtspSharpPlayer})");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (vm.UseRtspSharpPlayer)
                        {
                            var rtspSharp = this.FindControl<RtspSharpPlayer>("RtspSharpPlayer");
                            if (rtspSharp != null)
                            {
                                rtspSharp.FirstFrameReceived += OnRtspSharpFirstFrame;
                                rtspSharp.ZoomStateChanged += OnPlayerZoomStateChanged;
                                rtspSharp.ConnectionTimeout += OnRtspSharpConnectionTimeout;
                                rtspSharp.Play(vm.RtspUrl, vm.Rotation);
                                rtspSharp.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
                            }
                        }
                        else if (Player != null)
                        {
                            Player.FirstFrameReceived += OnPlayerFirstFrame;
                            Player.ZoomStateChanged += OnPlayerZoomStateChanged;
                            Player.Play(vm.RtspUrl, vm.Rotation);
                            Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
                        }
                    });
                }
            };

            // Обработка зума и пана для MJPEG
            if (MjpegContainer != null)
            {
                MjpegContainer.PointerWheelChanged += OnMjpegPointerWheelChanged;
                MjpegContainer.PointerPressed += OnMjpegPointerPressed;
                MjpegContainer.PointerMoved += OnMjpegPointerMoved;
                MjpegContainer.PointerReleased += OnMjpegPointerReleased;
                MjpegContainer.LayoutUpdated += (_, _) => UpdateMjpegContentLayout(vm);
            }
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(CameraPlayerViewModel.MjpegDisplayWidth) or nameof(CameraPlayerViewModel.MjpegDisplayHeight))
                    UpdateMjpegContentLayout(vm);
            };
        }
    }

    private void UpdateMjpegContentLayout(CameraPlayerViewModel vm)
    {
        if (MjpegContainer == null || MjpegContentPanel == null || MjpegCanvas == null) return;
        var bounds = MjpegContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || vm.MjpegDisplayWidth <= 0 || vm.MjpegDisplayHeight <= 0) return;
        GetRenderSize(bounds.Width, bounds.Height, vm.MjpegDisplayWidth, vm.MjpegDisplayHeight, out var rw, out var rh);
        var offsetX = (bounds.Width - rw) / 2;
        var offsetY = (bounds.Height - rh) / 2;
        MjpegContentPanel.Width = rw;
        MjpegContentPanel.Height = rh;
        Canvas.SetLeft(MjpegContentPanel, offsetX);
        Canvas.SetTop(MjpegContentPanel, offsetY);
    }

    private void OnPlayerFirstFrame()
    {
        System.Diagnostics.Debug.WriteLine("First frame received (LibVLC), notifying ViewModel");
        if (DataContext is not CameraPlayerViewModel vm) return;
        vm.OnRtspStarted();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (Player != null)
                Player.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnRtspSharpFirstFrame()
    {
        System.Diagnostics.Debug.WriteLine("First frame received (RtspSharp), notifying ViewModel");
        if (DataContext is not CameraPlayerViewModel vm) return;
        vm.OnRtspStarted();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var rtspSharp = this.FindControl<RtspSharpPlayer>("RtspSharpPlayer");
            rtspSharp?.SetZoomState(vm.ZoomLevel, vm.PanX, vm.PanY);
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnRtspSharpConnectionTimeout()
    {
        if (DataContext is CameraPlayerViewModel vm)
            vm.OnRtspConnectionTimeout();
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

    private void OnMjpegPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not CameraPlayerViewModel vm || MjpegContainer == null) return;
        var bounds = MjpegContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || vm.MjpegDisplayWidth <= 0 || vm.MjpegDisplayHeight <= 0) return;
        var pos = e.GetPosition(MjpegContainer);
        vm.ZoomAt(bounds.Width, bounds.Height, vm.MjpegDisplayWidth, vm.MjpegDisplayHeight, pos.X, pos.Y, e.Delta.Y);
        GetRenderSize(bounds.Width, bounds.Height, vm.MjpegDisplayWidth, vm.MjpegDisplayHeight, out var rw, out var rh);
        vm.ClampPan(bounds.Width, bounds.Height, rw, rh);
        e.Handled = true;
    }

    private void OnMjpegPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(MjpegContainer!).Properties.IsLeftButtonPressed && DataContext is CameraPlayerViewModel)
        {
            _mjpegDragging = true;
            _mjpegDragStart = e.GetPosition(MjpegContainer);
            e.Pointer.Capture(MjpegContainer);
            e.Handled = true;
        }
    }

    private void OnMjpegPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_mjpegDragging || DataContext is not CameraPlayerViewModel vm || MjpegContainer == null) return;
        var pos = e.GetPosition(MjpegContainer);
        var dx = pos.X - _mjpegDragStart.X;
        var dy = pos.Y - _mjpegDragStart.Y;
        _mjpegDragStart = pos;
        vm.PanBy(dx, dy);
        var bounds = MjpegContainer.Bounds;
        if (bounds.Width > 0 && bounds.Height > 0 && vm.MjpegDisplayWidth > 0 && vm.MjpegDisplayHeight > 0)
        {
            GetRenderSize(bounds.Width, bounds.Height, vm.MjpegDisplayWidth, vm.MjpegDisplayHeight, out var rw, out var rh);
            vm.ClampPan(bounds.Width, bounds.Height, rw, rh);
        }
        e.Handled = true;
    }

    private void OnMjpegPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mjpegDragging)
        {
            _mjpegDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private static void GetRenderSize(double viewW, double viewH, double imgW, double imgH, out double renderW, out double renderH)
    {
        var viewAspect = viewW / viewH;
        var imgAspect = imgW / imgH;
        if (viewAspect > imgAspect)
        {
            renderH = viewH;
            renderW = viewH * imgAspect;
        }
        else
        {
            renderW = viewW;
            renderH = viewW / imgAspect;
        }
    }
}

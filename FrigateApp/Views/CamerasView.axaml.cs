using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class CamerasView : UserControl
{
    private DateTime _lastVisibilityCheck = DateTime.MinValue;
    private readonly TimeSpan _visibilityCheckInterval = TimeSpan.FromMilliseconds(500);

    public CamerasView()
    {
        InitializeComponent();
        
        // Первоначальная проверка видимости после загрузки
        Loaded += (s, e) => UpdateCameraVisibility();
    }

    private void OnCameraTilePointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & KeyModifiers.Shift) == 0) return;
        if (sender is not Control control || control.DataContext is not CameraItemViewModel vm) return;
        if (!vm.IsZoomEnabled) return;
        var pos = e.GetPosition(control);
        var w = control.Bounds.Width;
        var h = control.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        vm.ZoomAt(pos.X, pos.Y, w, h, e.Delta.Y);
        e.Handled = true;
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Throttle: проверяем видимость не чаще раза в 500мс
        var now = DateTime.UtcNow;
        if ((now - _lastVisibilityCheck) < _visibilityCheckInterval)
            return;
        
        _lastVisibilityCheck = now;
        UpdateCameraVisibility();
    }

    private void UpdateCameraVisibility()
    {
        try
        {
            var scrollViewer = this.FindControl<ScrollViewer>("CameraScrollViewer");
            if (scrollViewer == null || DataContext is not CamerasViewModel camerasVm)
                return;

            var viewport = new Rect(
                scrollViewer.Offset.X,
                scrollViewer.Offset.Y,
                scrollViewer.Viewport.Width,
                scrollViewer.Viewport.Height
            );

            // Добавляем буфер ±200px для pre-loading камер рядом с viewport
            var expandedViewport = viewport.Inflate(200);

            // Проходим по всем камерам и обновляем их видимость
            foreach (var cameraVm in camerasVm.Cameras)
            {
                // Ищем визуальный элемент камеры
                var container = FindCameraContainer(scrollViewer, cameraVm);
                if (container == null)
                {
                    // Если не найден - считаем невидимым
                    cameraVm.SetVisibility(false);
                    continue;
                }

                // Получаем bounds относительно ScrollViewer
                var bounds = container.Bounds;
                var relativeBounds = container.TranslatePoint(new Point(0, 0), scrollViewer);
                
                if (relativeBounds.HasValue)
                {
                    var cameraBounds = new Rect(relativeBounds.Value, bounds.Size);
                    var isVisible = expandedViewport.Intersects(cameraBounds);
                    cameraVm.SetVisibility(isVisible);
                }
                else
                {
                    cameraVm.SetVisibility(false);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error updating camera visibility: {ex.Message}");
        }
    }

    private Control? FindCameraContainer(Control parent, CameraItemViewModel cameraVm)
    {
        // Рекурсивный поиск контейнера камеры по DataContext
        var visualChildren = parent.GetVisualDescendants().OfType<Control>();
        foreach (var child in visualChildren)
        {
            if (child.DataContext == cameraVm && child is Button)
                return child;
        }
        return null;
    }
}

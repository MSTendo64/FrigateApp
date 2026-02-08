using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace FrigateApp.Controls;

public partial class TimelineControl : UserControl
{
    public static readonly StyledProperty<double> RangeStartProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(RangeStart), 0);

    public static readonly StyledProperty<double> RangeEndProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(RangeEnd), 86400);

    public static readonly StyledProperty<IEnumerable<TimelineSegmentItem>?> SegmentsProperty =
        AvaloniaProperty.Register<TimelineControl, IEnumerable<TimelineSegmentItem>?>(nameof(Segments), null);

    public double RangeStart
    {
        get => GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public double RangeEnd
    {
        get => GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    public IEnumerable<TimelineSegmentItem>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <summary>Событие: пользователь кликнул по таймлайну — передаётся unix-время.</summary>
    public event Action<double>? TimeClicked;

    private bool _isUpdatingSegments;

    public TimelineControl()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var canvas = SegmentCanvas;
        if (canvas != null)
        {
            canvas.SizeChanged += OnCanvasSizeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        var canvas = SegmentCanvas;
        if (canvas != null)
            canvas.SizeChanged -= OnCanvasSizeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ScheduleUpdateSegments();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RangeStartProperty || change.Property == RangeEndProperty || change.Property == SegmentsProperty)
            ScheduleUpdateSegments();
    }

    private void ScheduleUpdateSegments()
    {
        Dispatcher.UIThread.Post(UpdateSegments, DispatcherPriority.Background);
    }

    private void UpdateSegments()
    {
        if (_isUpdatingSegments) return;
        var canvas = SegmentCanvas;
        if (canvas == null) return;
        var width = canvas.Bounds.Width;
        var height = canvas.Bounds.Height;
        if (width <= 0 || height <= 0) return;
        var range = RangeEnd - RangeStart;
        if (range <= 0) return;

        _isUpdatingSegments = true;
        try
        {
            canvas.Children.Clear();
            var segments = Segments;
            if (segments != null)
            {
                foreach (var seg in segments)
                {
                    var x = (seg.StartUnix - RangeStart) / range * width;
                    var segW = (seg.EndUnix - seg.StartUnix) / range * width;
                    if (segW < 1) segW = 1;
                    var rect = new Avalonia.Controls.Shapes.Rectangle
                    {
                        Fill = Brushes.Crimson,
                        Width = segW,
                        Height = height,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(rect, x);
                    Canvas.SetTop(rect, 0);
                    canvas.Children.Add(rect);
                }
            }
        }
        finally
        {
            _isUpdatingSegments = false;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);
        var range = RangeEnd - RangeStart;
        if (range <= 0) return;
        var w = Bounds.Width;
        if (w <= 0) return;
        var unixTime = RangeStart + (pos.X / w) * range;
        TimeClicked?.Invoke(unixTime);
        e.Handled = true;
    }
}

/// <summary>Один отрезок на таймлайне (unix start/end).</summary>
public class TimelineSegmentItem
{
    public double StartUnix { get; set; }
    public double EndUnix { get; set; }
}

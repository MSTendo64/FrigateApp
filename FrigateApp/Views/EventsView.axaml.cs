using Avalonia.Controls;
using FrigateApp.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Views;

public partial class EventsView : UserControl
{
    public EventsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var timeline = this.FindControl<TimelineControl>("Timeline");
        if (timeline != null && DataContext is EventsViewModel vm)
        {
            timeline.TimeClicked -= OnTimelineTimeClicked;
            timeline.TimeClicked += OnTimelineTimeClicked;
        }
    }

    private void OnTimelineTimeClicked(double unixTime)
    {
        if (DataContext is EventsViewModel vm)
            vm.OnTimelineClicked(unixTime);
    }
}

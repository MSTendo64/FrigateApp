using Avalonia.Controls;
using FrigateApp.ViewModels;

namespace FrigateApp.Controls;

public partial class CameraTileContent : UserControl
{
    public CameraTileContent()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is CameraItemViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(CameraItemViewModel.VideoFilePath)) return;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => UpdateVideoVisibility(vm));
            };
            UpdateVideoVisibility(vm);
        }
    }

    private void UpdateVideoVisibility(CameraItemViewModel vm)
    {
        var path = vm.VideoFilePath;
        if (!string.IsNullOrEmpty(path) && TilePlayer != null && PlaceholderImage != null)
        {
            PlaceholderImage.IsVisible = false;
            TilePlayer.IsVisible = true;
            TilePlayer.PlayMediaUrl(path, vm.Rotation);
        }
        else if (TilePlayer != null && PlaceholderImage != null)
        {
            TilePlayer.Stop();
            TilePlayer.IsVisible = false;
            PlaceholderImage.IsVisible = true;
        }
    }
}

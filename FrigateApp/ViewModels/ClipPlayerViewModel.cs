using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FrigateApp.ViewModels;

public partial class ClipPlayerViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _mediaUrl = "";

    [ObservableProperty]
    private string _title = "Клип";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _showFallbackMessage;

    private readonly Action _onBack;

    public ClipPlayerViewModel(string mediaUrl, string title, Action onBack)
    {
        _mediaUrl = mediaUrl ?? "";
        _title = title ?? "Клип";
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
    }

    [RelayCommand]
    private void Back()
    {
        _onBack();
    }
}

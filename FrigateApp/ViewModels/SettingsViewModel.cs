using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

/// <summary>
/// ViewModel экрана настроек приложения.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Настройки";

    private readonly Action _onBack;
    private readonly UserPreferencesService _prefs;

    public SettingsViewModel(Action onBack, UserPreferencesService? prefs = null)
    {
        _onBack = onBack ?? throw new ArgumentNullException(nameof(onBack));
        _prefs = prefs ?? new UserPreferencesService();
    }

    [RelayCommand]
    private void Back()
    {
        _onBack();
    }
}

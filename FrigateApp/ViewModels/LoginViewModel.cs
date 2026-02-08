using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FrigateApp.Models;
using FrigateApp.Services;

namespace FrigateApp.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _serverUrl = "http://localhost:5000/api";

    [ObservableProperty]
    private string _username = "";

    [ObservableProperty]
    private string _password = "";

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _rememberPassword = true;

    [ObservableProperty]
    private SavedProfile? _selectedProfile;

    [ObservableProperty]
    private ObservableCollection<SavedProfile> _savedProfiles = new();

    private readonly Action<FrigateApiService> _onLoginSuccess;
    private readonly UserPreferencesService _prefs;

    public LoginViewModel(Action<FrigateApiService> onLoginSuccess, UserPreferencesService? prefs = null)
    {
        _onLoginSuccess = onLoginSuccess ?? throw new ArgumentNullException(nameof(onLoginSuccess));
        _prefs = prefs ?? new UserPreferencesService();
        LoadSavedProfiles();
    }

    private void LoadSavedProfiles()
    {
        SavedProfiles.Clear();
        foreach (var p in _prefs.Profiles)
            SavedProfiles.Add(p);
    }

    partial void OnSelectedProfileChanged(SavedProfile? value)
    {
        if (value == null) return;
        ServerUrl = value.ServerUrl.TrimEnd('/');
        Username = value.Username;
        Password = value.Password ?? "";
    }

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorMessage = "";
        IsBusy = true;
        LoginCommand.NotifyCanExecuteChanged();

        try
        {
            var baseUrl = ServerUrl.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ErrorMessage = "Введите адрес сервера (например: http://192.168.1.10:5000/api)";
                return;
            }

            if (string.IsNullOrWhiteSpace(Username))
            {
                ErrorMessage = "Введите имя пользователя";
                return;
            }

            var service = new FrigateApiService(baseUrl);
            await service.LoginAsync(Username.Trim(), Password).ConfigureAwait(true);

            if (RememberPassword)
            {
                _prefs.AddOrUpdate(new SavedProfile
                {
                    ServerUrl = baseUrl,
                    Username = Username.Trim(),
                    Password = Password,
                    DisplayName = null
                });
            }
            else
            {
                _prefs.AddOrUpdate(new SavedProfile
                {
                    ServerUrl = baseUrl,
                    Username = Username.Trim(),
                    Password = null,
                    DisplayName = null
                });
            }

            _onLoginSuccess(service);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            LoginCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private void SaveProfile()
    {
        var baseUrl = ServerUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(Username)) return;
        _prefs.AddOrUpdate(new SavedProfile
        {
            ServerUrl = baseUrl,
            Username = Username.Trim(),
            Password = RememberPassword ? Password : null,
            DisplayName = null
        });
        LoadSavedProfiles();
    }

    private bool CanSaveProfile() => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(Username);

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile == null) return;
        _prefs.Remove(SelectedProfile.ServerUrl, SelectedProfile.Username);
        LoadSavedProfiles();
        SelectedProfile = SavedProfiles.FirstOrDefault();
        if (SelectedProfile != null)
        {
            ServerUrl = SelectedProfile.ServerUrl;
            Username = SelectedProfile.Username;
            Password = SelectedProfile.Password ?? "";
        }
    }

    private bool CanDeleteProfile() => SelectedProfile != null;

    private bool CanLogin() => !IsBusy;
}

using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FrigateApp.ViewModels;

/// <summary>
/// Пункт боковой панели: "Все камеры" (Id=null) или группа камер (Id=ключ группы).
/// </summary>
public partial class SidebarItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _id;

    [ObservableProperty]
    private string _displayName = "";

    [ObservableProperty]
    private bool _isSelected;

    public ICommand? SelectCommand { get; set; }
}

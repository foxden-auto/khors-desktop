using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;

namespace Khors.App.ViewModels;

/// <summary>Разделы окна в боковом меню.</summary>
public enum AppPage
{
    Connection,
    Servers,
    Settings,
    Log,
    About,
}

public sealed partial class MainWindowViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnectionPage), nameof(IsServersPage), nameof(IsSettingsPage), nameof(IsLogPage), nameof(IsAboutPage))]
    public partial AppPage CurrentPage { get; set; }

    public bool IsConnectionPage => CurrentPage == AppPage.Connection;

    public bool IsServersPage => CurrentPage == AppPage.Servers;

    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    public bool IsLogPage => CurrentPage == AppPage.Log;

    public bool IsAboutPage => CurrentPage == AppPage.About;

    public static string AppVersionText => Localizer.Format("AboutVersionFormat", App.AppVersion);

    [RelayCommand]
    private void Navigate(AppPage page) => CurrentPage = page;

    [RelayCommand]
    private void DismissMessage() => Message = null;
}

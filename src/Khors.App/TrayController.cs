using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Khors.App.Services;
using Khors.App.ViewModels;

namespace Khors.App;

/// <summary>Иконка в трее: подключить/отключить, выбор профиля, показать окно, выход (docs/SPEC.md, 4.9).</summary>
internal sealed class TrayController : IDisposable
{
    private readonly Application _application;
    private readonly MainWindowViewModel _viewModel;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _toggle;
    private readonly NativeMenu _profilesMenu = new();

    public TrayController(Application application, MainWindowViewModel viewModel, Action showWindow, Action exit)
    {
        _application = application;
        _viewModel = viewModel;

        _toggle = new NativeMenuItem(viewModel.ConnectButtonText) { Command = viewModel.ToggleConnectionCommand };
        var show = new NativeMenuItem(Localizer.Get("TrayShow"));
        show.Click += (_, _) => showWindow();
        var quit = new NativeMenuItem(Localizer.Get("TrayExit"));
        quit.Click += (_, _) => exit();

        var menu = new NativeMenu();
        menu.Items.Add(_toggle);
        menu.Items.Add(new NativeMenuItem(Localizer.Get("ProfilesHeader")) { Menu = _profilesMenu });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(show);
        menu.Items.Add(quit);

        // Имя сборки — khors-desktop (не совпадает с пространством имён), поэтому берём его из сборки.
        using var iconStream = AssetLoader.Open(new Uri($"avares://{typeof(TrayController).Assembly.GetName().Name}/Assets/khors.ico"));
        _icon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            Menu = menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => showWindow();
        TrayIcon.SetIcons(application, [_icon]);

        RebuildProfiles();
        UpdateStatus();
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.Profiles.CollectionChanged += OnProfilesChanged;
    }

    public void Dispose()
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Profiles.CollectionChanged -= OnProfilesChanged;
        _icon.IsVisible = false;
        TrayIcon.SetIcons(_application, []);
        _icon.Dispose();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.ConnectButtonText):
            case nameof(MainWindowViewModel.StatusText):
            case nameof(MainWindowViewModel.ActiveProfileName):
                UpdateStatus();
                break;
            case nameof(MainWindowViewModel.SelectedProfile):
                RebuildProfiles();
                break;
        }
    }

    private void OnProfilesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildProfiles();

    private void UpdateStatus()
    {
        _toggle.Header = _viewModel.ConnectButtonText;
        var status = _viewModel.ActiveProfileName is { } name ? $"{_viewModel.StatusText}: {name}" : _viewModel.StatusText;
        _icon.ToolTipText = Localizer.Format("TrayTooltipFormat", status);
    }

    private void RebuildProfiles()
    {
        _profilesMenu.Items.Clear();
        foreach (var profile in _viewModel.Profiles)
        {
            var item = new NativeMenuItem(profile.Name)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = profile == _viewModel.SelectedProfile,
            };
            item.Click += (_, _) => _viewModel.SelectedProfile = profile;
            _profilesMenu.Items.Add(item);
        }
    }
}

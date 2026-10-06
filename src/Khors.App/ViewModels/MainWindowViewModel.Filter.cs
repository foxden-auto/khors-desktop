using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;

namespace Khors.App.ViewModels;

/// <summary>Вкладки «Все / Избранные» и поиск по списку серверов.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Профили, видимые в списках окна; <see cref="Profiles"/> — все (трей, «Авто», тест задержки).</summary>
    public ObservableCollection<ProfileItemViewModel> VisibleProfiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAllProfiles))]
    public partial bool ShowFavoritesOnly { get; set; }

    public bool ShowAllProfiles
    {
        get => !ShowFavoritesOnly;
        set => ShowFavoritesOnly = !value;
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Выбор в списке. Список сбрасывает выделение, когда фильтр прячет выбранный профиль, — это не отменяет выбор
    /// профиля для подключения, поэтому <c>null</c> из списка игнорируется.
    /// </summary>
    public ProfileItemViewModel? ListSelectedProfile
    {
        get => SelectedProfile;
        set
        {
            if (value is not null)
            {
                SelectedProfile = value;
            }
        }
    }

    /// <summary>Профили есть, но под фильтр не подходит ни один.</summary>
    public bool HasNoVisibleProfiles => HasProfiles && VisibleProfiles.Count == 0;

    public string EmptyFilterText => Localizer.Get(ShowFavoritesOnly && string.IsNullOrWhiteSpace(SearchText) ? "FavoritesEmpty" : "SearchNoResults");

    partial void OnShowFavoritesOnlyChanged(bool value) => ApplyProfileFilter();

    partial void OnSearchTextChanged(string value) => ApplyProfileFilter();

    private void ApplyProfileFilter()
    {
        VisibleProfiles.Clear();
        foreach (var item in Profiles.Where(p => (!ShowFavoritesOnly || p.IsFavorite) && p.Matches(SearchText ?? string.Empty)))
        {
            VisibleProfiles.Add(item);
        }

        OnPropertyChanged(nameof(HasNoVisibleProfiles));
        OnPropertyChanged(nameof(EmptyFilterText));
        OnPropertyChanged(nameof(ListSelectedProfile));
    }
}

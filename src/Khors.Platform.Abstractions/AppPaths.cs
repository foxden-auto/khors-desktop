namespace Khors.Platform;

/// <summary>Каталоги данных KHORS на этой платформе.</summary>
/// <param name="DataDirectory">Данные пользователя: профили, настройки (Windows — <c>%APPDATA%\KHORS</c>).</param>
/// <param name="StateDirectory">Журналы отката изменений системы.</param>
public sealed record AppPaths(string DataDirectory, string StateDirectory)
{
    public string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Гео-базы окна (режим «Системный прокси»).</summary>
    public string GeoDirectory => Path.Combine(DataDirectory, "geo");
}

/// <summary>Каталоги данных службы KHORS (Windows — <c>%ProgramData%\KHORS</c>): писать туда может только служба и администраторы.</summary>
public sealed record ServicePaths(string DataDirectory)
{
    /// <summary>Гео-базы службы (режим TUN).</summary>
    public string GeoDirectory => Path.Combine(DataDirectory, "geo");
}

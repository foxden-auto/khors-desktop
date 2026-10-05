namespace Khors.Platform;

/// <summary>Каталоги данных KHORS на этой платформе.</summary>
/// <param name="DataDirectory">Данные пользователя: профили, настройки (Windows — <c>%APPDATA%\KHORS</c>).</param>
/// <param name="StateDirectory">Журналы отката изменений системы.</param>
public sealed record AppPaths(string DataDirectory, string StateDirectory)
{
    public string ProfilesFile => Path.Combine(DataDirectory, "profiles.json");

    public string SettingsFile => Path.Combine(DataDirectory, "settings.json");
}

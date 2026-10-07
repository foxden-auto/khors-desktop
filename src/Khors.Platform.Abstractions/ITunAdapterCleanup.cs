namespace Khors.Platform;

/// <summary>
/// Удаление адаптера туннеля KHORS, оставшегося в системе, в том числе отключённого или битого (ROADMAP 4.4):
/// при сбросе сетевых настроек, при удалении службы и перед повторным запуском TUN. Требует прав службы.
/// Адаптеры других программ с тем же драйвером не трогаются.
/// </summary>
public interface ITunAdapterCleanup
{
    /// <summary>Удаляет все адаптеры туннеля с именем <paramref name="name"/>; возвращает их число (0 — не было).</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">Адаптер есть, но удалить его не удалось.</exception>
    int RemoveAdapters(string name);
}

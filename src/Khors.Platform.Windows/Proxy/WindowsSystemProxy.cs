using System.Text.Json;
using System.Text.Json.Serialization;

namespace Khors.Platform.Windows.Proxy;

/// <summary>
/// Системный прокси Windows (WinINet, подключение LAN). Перед первым изменением прежние настройки
/// записываются в журнал <c>%APPDATA%\KHORS\state\system-proxy.json</c>; откат возвращает их без потерь
/// (в том числе чужой прокси, PAC и автоопределение).
/// </summary>
public sealed class WindowsSystemProxy : ISystemProxy
{
    private const int JournalSchemaVersion = 1;

    private readonly IWinInetProxySettings _settings;
    private readonly string _journalPath;
    private readonly Lock _lock = new();

    public WindowsSystemProxy(string stateDirectory)
        : this(new WinInetProxySettings(), stateDirectory)
    {
    }

    internal WindowsSystemProxy(IWinInetProxySettings settings, string stateDirectory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrEmpty(stateDirectory);
        _settings = settings;
        _journalPath = Path.Combine(stateDirectory, "system-proxy.json");
    }

    public void Enable(SystemProxySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var server = $"{settings.Host}:{settings.Port}";

        lock (_lock)
        {
            // Журнал уже есть — прокси уже наш (например, смена порта): исходные настройки не перезаписываем.
            var journal = ReadJournal(out _) ?? new SystemProxyJournal(JournalSchemaVersion, _settings.Read(), server, DateTimeOffset.Now);
            journal = journal with { AppliedServer = server };
            WriteJournal(journal);

            _settings.Write(new WinInetProxyState(
                WinInetProxyState.ProxyTypeDirect | WinInetProxyState.ProxyTypeProxy,
                server,
                string.Join(';', settings.Bypass),
                journal.Previous.AutoConfigUrl));
        }
    }

    public bool Restore()
    {
        lock (_lock)
        {
            var journal = ReadJournal(out _);
            if (journal is null)
            {
                return false;
            }

            _settings.Write(journal.Previous);
            File.Delete(_journalPath);
            return true;
        }
    }

    public SystemProxyRecovery RecoverAfterCrash()
    {
        lock (_lock)
        {
            var journal = ReadJournal(out var corrupted);
            if (corrupted)
            {
                File.Delete(_journalPath);
                return SystemProxyRecovery.JournalCorrupted;
            }

            if (journal is null)
            {
                return SystemProxyRecovery.NothingToRecover;
            }

            var current = _settings.Read();
            var stillOurs = current.UsesProxyServer
                && string.Equals(current.ProxyServer, journal.AppliedServer, StringComparison.OrdinalIgnoreCase);

            if (stillOurs)
            {
                _settings.Write(journal.Previous);
            }

            File.Delete(_journalPath);
            return stillOurs ? SystemProxyRecovery.Restored : SystemProxyRecovery.ChangedByUser;
        }
    }

    private SystemProxyJournal? ReadJournal(out bool corrupted)
    {
        corrupted = false;
        if (!File.Exists(_journalPath))
        {
            return null;
        }

        try
        {
            var journal = JsonSerializer.Deserialize(File.ReadAllText(_journalPath), SystemProxyJsonContext.Default.SystemProxyJournal);
            if (journal is { SchemaVersion: JournalSchemaVersion })
            {
                return journal;
            }
        }
        catch (JsonException)
        {
        }

        corrupted = true;
        return null;
    }

    /// <summary>Запись через временный файл и переименование: журнал не бывает записан наполовину.</summary>
    private void WriteJournal(SystemProxyJournal journal)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
        var temporary = _journalPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, SystemProxyJsonContext.Default.SystemProxyJournal));
        File.Move(temporary, _journalPath, overwrite: true);
    }
}

/// <param name="Previous">Настройки до первого включения прокси KHORS.</param>
/// <param name="AppliedServer">Прокси, выставленный KHORS, — по нему видно, что настройки всё ещё наши.</param>
internal sealed record SystemProxyJournal(int SchemaVersion, WinInetProxyState Previous, string AppliedServer, DateTimeOffset CreatedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SystemProxyJournal))]
internal sealed partial class SystemProxyJsonContext : JsonSerializerContext;

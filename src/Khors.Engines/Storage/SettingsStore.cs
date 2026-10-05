using Khors.Core.Storage;

namespace Khors.Engines.Storage;

/// <summary>Настройки приложения в <c>settings.json</c>. Повреждённый или отсутствующий файл — настройки по умолчанию.</summary>
public sealed class SettingsStore
{
    private readonly DocumentFile<AppSettings> _file;
    private readonly Lock _lock = new();
    private AppSettings _current;

    private SettingsStore(DocumentFile<AppSettings> file, DocumentLoadResult<AppSettings> load)
    {
        _file = file;
        LoadResult = load;
        _current = load.Value ?? new AppSettings();
    }

    public event EventHandler? Changed;

    public DocumentLoadResult<AppSettings> LoadResult { get; }

    public bool IsReadOnly => LoadResult.Status == StorageLoadStatus.FutureVersion;

    public AppSettings Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public static SettingsStore Open(string path, TimeProvider? time = null)
    {
        var file = new DocumentFile<AppSettings>(path, StorageJson.ParseSettings, StorageJson.SerializeSettings, time);
        return new SettingsStore(file, file.Load());
    }

    /// <summary>Изменяет и сразу сохраняет настройки. Файл новой версии KHORS не перезаписывается.</summary>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_lock)
        {
            _current = change(_current) with { SchemaVersion = AppSettings.CurrentSchemaVersion };
            if (!IsReadOnly)
            {
                _file.Save(_current);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Storage;

namespace Khors.Engines.Storage;

/// <summary>
/// Профили пользователя: в памяти и в <c>profiles.json</c>. Каждое изменение сразу сохраняется.
/// Если файл записан более новой версией KHORS — хранилище только для чтения.
/// </summary>
public sealed class ProfileRepository
{
    private readonly DocumentFile<ProfileDocument> _file;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private readonly List<Profile> _profiles;

    private ProfileRepository(DocumentFile<ProfileDocument> file, TimeProvider time, DocumentLoadResult<ProfileDocument> load)
    {
        _file = file;
        _time = time;
        LoadResult = load;
        _profiles = load.Value is { } document ? [.. document.Profiles] : [];
    }

    /// <summary>Профили изменились. Вызывается в потоке, выполнившем изменение.</summary>
    public event EventHandler? Changed;

    /// <summary>Как прошло чтение файла — для сообщения пользователю (восстановлено из копии, файл повреждён и т.п.).</summary>
    public DocumentLoadResult<ProfileDocument> LoadResult { get; }

    public bool IsReadOnly => LoadResult.Status == StorageLoadStatus.FutureVersion;

    public IReadOnlyList<Profile> Profiles
    {
        get
        {
            lock (_lock)
            {
                return [.. _profiles];
            }
        }
    }

    public static ProfileRepository Open(string path, TimeProvider? time = null)
    {
        var file = new DocumentFile<ProfileDocument>(path, StorageJson.ParseProfiles, StorageJson.SerializeProfiles, time);
        return new ProfileRepository(file, time ?? TimeProvider.System, file.Load());
    }

    public Profile? Find(Guid id)
    {
        lock (_lock)
        {
            return _profiles.Find(p => p.Id == id);
        }
    }

    /// <summary>Импорт одной или нескольких ссылок (например, из буфера обмена); дубликаты пропускаются.</summary>
    public ImportResult Import(string text)
    {
        ImportResult result;
        lock (_lock)
        {
            ThrowIfReadOnly();
            result = ProfileImporter.Import(text, _profiles, Guid.NewGuid, _time.GetUtcNow());
            if (result.Added.Count == 0)
            {
                return result;
            }

            _profiles.AddRange(result.Added);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void Update(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_lock)
        {
            ThrowIfReadOnly();
            var index = _profiles.FindIndex(p => p.Id == profile.Id);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Profile {profile.Id} not found.");
            }

            _profiles[index] = profile with { UpdatedAt = _time.GetUtcNow() };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool Remove(Guid id)
    {
        lock (_lock)
        {
            ThrowIfReadOnly();
            if (_profiles.RemoveAll(p => p.Id == id) == 0)
            {
                return false;
            }

            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Save() => _file.Save(new ProfileDocument { Profiles = new EquatableArray<Profile>(_profiles) });

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("profiles.json was written by a newer KHORS version and is read-only.");
        }
    }
}

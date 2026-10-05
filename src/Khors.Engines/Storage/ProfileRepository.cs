using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Core.Subscriptions;

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
    private readonly List<Subscription> _subscriptions;

    private ProfileRepository(DocumentFile<ProfileDocument> file, TimeProvider time, DocumentLoadResult<ProfileDocument> load)
    {
        _file = file;
        _time = time;
        LoadResult = load;
        _profiles = load.Value is { } document ? [.. document.Profiles] : [];
        _subscriptions = load.Value is { } withSubscriptions ? [.. withSubscriptions.Subscriptions] : [];
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

    public IReadOnlyList<Subscription> Subscriptions
    {
        get
        {
            lock (_lock)
            {
                return [.. _subscriptions];
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

    public Subscription? FindSubscription(Guid id)
    {
        lock (_lock)
        {
            return _subscriptions.Find(s => s.Id == id);
        }
    }

    /// <summary>Добавляет подписку (если такой адрес уже есть — возвращает имеющуюся). Профили появятся после обновления.</summary>
    public Subscription AddSubscription(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Subscription URL must be http or https.", nameof(url));
        }

        Subscription subscription;
        lock (_lock)
        {
            ThrowIfReadOnly();
            var existing = _subscriptions.Find(s => s.Url.Value == url.AbsoluteUri);
            if (existing is not null)
            {
                return existing;
            }

            subscription = new Subscription { Id = Guid.NewGuid(), Name = url.Host, Url = new Secret(url.AbsoluteUri) };
            _subscriptions.Add(subscription);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return subscription;
    }

    /// <summary>
    /// Применяет ответ подписки: слияние профилей (см. <see cref="SubscriptionMerge"/>), трафик, срок, интервал, имя.
    /// Профили подписки остаются на месте первого из них в общем списке.
    /// </summary>
    public SubscriptionMergeResult ApplySubscriptionUpdate(
        Guid subscriptionId,
        IReadOnlyCollection<Profile> incoming,
        SubscriptionUserInfo? userInfo,
        int? updateIntervalHours,
        string? title)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        SubscriptionMergeResult merge;
        lock (_lock)
        {
            ThrowIfReadOnly();
            var index = _subscriptions.FindIndex(s => s.Id == subscriptionId);
            if (index < 0)
            {
                throw new KeyNotFoundException($"Subscription {subscriptionId} not found.");
            }

            var now = _time.GetUtcNow();
            var subscription = _subscriptions[index] with
            {
                Name = string.IsNullOrWhiteSpace(title) ? _subscriptions[index].Name : title,
                UpdatedAt = now,
                LastAttemptAt = now,
                UserInfo = userInfo,
                UpdateIntervalHours = updateIntervalHours,
                LastError = null,
            };
            _subscriptions[index] = subscription;

            var current = _profiles.Where(p => p.SubscriptionId == subscriptionId).ToList();
            merge = SubscriptionMerge.Merge(current, incoming, subscription, Guid.NewGuid, now);

            var position = current.Count > 0 ? _profiles.IndexOf(current[0]) : _profiles.Count;
            _profiles.RemoveAll(p => p.SubscriptionId == subscriptionId);
            _profiles.InsertRange(Math.Min(position, _profiles.Count), merge.Profiles);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return merge;
    }

    public void MarkSubscriptionFailed(Guid subscriptionId, SubscriptionUpdateError error)
    {
        lock (_lock)
        {
            ThrowIfReadOnly();
            var index = _subscriptions.FindIndex(s => s.Id == subscriptionId);
            if (index < 0)
            {
                return;
            }

            _subscriptions[index] = _subscriptions[index] with { LastError = error, LastAttemptAt = _time.GetUtcNow() };
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Удаляет подписку вместе с её профилями.</summary>
    public bool RemoveSubscription(Guid subscriptionId)
    {
        lock (_lock)
        {
            ThrowIfReadOnly();
            if (_subscriptions.RemoveAll(s => s.Id == subscriptionId) == 0)
            {
                return false;
            }

            _profiles.RemoveAll(p => p.SubscriptionId == subscriptionId);
            Save();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void Save() => _file.Save(new ProfileDocument
    {
        Profiles = new EquatableArray<Profile>(_profiles),
        Subscriptions = new EquatableArray<Subscription>(_subscriptions),
    });

    private void ThrowIfReadOnly()
    {
        if (IsReadOnly)
        {
            throw new InvalidOperationException("profiles.json was written by a newer KHORS version and is read-only.");
        }
    }
}

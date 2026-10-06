using Khors.Core.Import;
using Khors.Core.Profiles;

namespace Khors.Core.Subscriptions;

/// <param name="Profiles">Итоговые профили подписки в порядке ответа сервера.</param>
/// <param name="Removed">Id профилей, которых больше нет в подписке.</param>
public sealed record SubscriptionMergeResult(EquatableArray<Profile> Profiles, int Added, int Renamed, EquatableArray<Guid> Removed);

/// <summary>
/// Обновление профилей подписки. То же подключение (совпадает всё, кроме имени и служебных полей)
/// сохраняет свой Id и выбранное пользователем ядро — выбор пользователя и замеры не теряются. Чистая функция.
/// </summary>
public static class SubscriptionMerge
{
    public static SubscriptionMergeResult Merge(
        IReadOnlyCollection<Profile> current,
        IReadOnlyCollection<Profile> incoming,
        Subscription subscription,
        Func<Guid> newId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(newId);

        var byIdentity = new Dictionary<Profile, Queue<Profile>>();
        foreach (var profile in current)
        {
            var identity = ProfileImporter.Identity(profile);
            if (!byIdentity.TryGetValue(identity, out var queue))
            {
                byIdentity[identity] = queue = new Queue<Profile>();
            }

            queue.Enqueue(profile);
        }

        var result = new List<Profile>();
        var kept = new HashSet<Guid>();
        int added = 0, renamed = 0;
        foreach (var profile in incoming)
        {
            var fromSubscription = profile with { SubscriptionId = subscription.Id, Group = subscription.Name };
            if (byIdentity.TryGetValue(ProfileImporter.Identity(profile), out var queue) && queue.TryDequeue(out var existing))
            {
                var changed = existing.Name != profile.Name || existing.Group != subscription.Name;
                renamed += existing.Name != profile.Name ? 1 : 0;
                result.Add(fromSubscription with { Id = existing.Id, Core = existing.Core, IsFavorite = existing.IsFavorite, UpdatedAt = changed ? now : existing.UpdatedAt });
                kept.Add(existing.Id);
            }
            else
            {
                result.Add(fromSubscription with { Id = newId(), UpdatedAt = now });
                added++;
            }
        }

        var removed = current.Where(p => !kept.Contains(p.Id)).Select(p => p.Id).ToArray();
        return new SubscriptionMergeResult(new EquatableArray<Profile>(result), added, renamed, removed);
    }
}

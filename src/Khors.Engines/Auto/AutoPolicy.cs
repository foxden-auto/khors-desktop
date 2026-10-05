using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Latency;

namespace Khors.Engines.Auto;

/// <summary>
/// Правила группы «Авто» (docs/SPEC.md, 4.3): из каких профилей выбирать, как их упорядочить и когда
/// стоит переключиться на другой сервер. Переключение рвёт соединения, поэтому работающий сервер меняется
/// только на заметно более быстрый — шум замеров не должен дёргать подключение.
/// </summary>
public static class AutoPolicy
{
    /// <summary>Во сколько раз другой сервер должен быть быстрее текущего.</summary>
    public const double MinSpeedup = 1.5;

    /// <summary>И на сколько быстрее в абсолютном выражении.</summary>
    public static TimeSpan MinGain { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Профиль без ошибок, который может запустить хотя бы одно ядро.</summary>
    public static bool IsCandidate(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return !ProfileValidator.Validate(profile).Any(i => i.Severity == ProfileIssueSeverity.Error)
            && CoreSelection.Select(profile).IsSupported;
    }

    /// <summary>Ответившие профили по возрастанию задержки; при равенстве — в порядке списка.</summary>
    public static IReadOnlyList<Profile> Rank(IReadOnlyList<Profile> candidates, IReadOnlyDictionary<Guid, LatencyResult> results)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(results);
        return
        [
            .. candidates
                .Select((profile, index) => (profile, index, delay: results.GetValueOrDefault(profile.Id) is { Status: LatencyStatus.Ok, Delay: { } d } ? d : (TimeSpan?)null))
                .Where(c => c.delay is not null)
                .OrderBy(c => c.delay)
                .ThenBy(c => c.index)
                .Select(c => c.profile),
        ];
    }

    /// <summary>Другой сервер заметно быстрее текущего: в <see cref="MinSpeedup"/> раза и не меньше чем на <see cref="MinGain"/>.</summary>
    public static bool IsNotablyFaster(TimeSpan candidate, TimeSpan current) =>
        current.Ticks >= candidate.Ticks * MinSpeedup && current - candidate >= MinGain;
}

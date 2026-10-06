using Khors.Engines.Traffic;

namespace Khors.App.Services;

/// <summary>Скорость за секунду: байт в секунду на отправку и загрузку.</summary>
public readonly record struct TrafficSample(double UpPerSecond, double DownPerSecond);

/// <summary>
/// Скорость по счётчикам ядра: разница соседних замеров, делённая на прошедшее время; хранит последние
/// <see cref="Capacity"/> значений для графика. Счётчики уменьшились (ядро перезапущено) — отсчёт начинается заново.
/// </summary>
public sealed class TrafficMeter(int capacity = 60)
{
    private static readonly int[] s_steps = [1, 2, 5];

    private readonly Queue<TrafficSample> _samples = new();
    private TrafficCounters? _last;
    private DateTimeOffset _lastAt;

    public int Capacity { get; } = capacity;

    /// <summary>Отправлено и получено с подключения; <c>null</c> — замеров ещё не было.</summary>
    public TrafficCounters? Total => _last;

    /// <summary>Последняя скорость (нули, пока замеров меньше двух).</summary>
    public TrafficSample Current { get; private set; }

    /// <summary>Скорости от старых к новым, не больше <see cref="Capacity"/>.</summary>
    public IReadOnlyList<TrafficSample> Samples => [.. _samples];

    public void Add(TrafficCounters counters, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(counters);
        if (_last is { } last && at > _lastAt && counters.Uplink >= last.Uplink && counters.Downlink >= last.Downlink)
        {
            var seconds = (at - _lastAt).TotalSeconds;
            Current = new TrafficSample((counters.Uplink - last.Uplink) / seconds, (counters.Downlink - last.Downlink) / seconds);
            _samples.Enqueue(Current);
            while (_samples.Count > Capacity)
            {
                _samples.Dequeue();
            }
        }
        else
        {
            Current = default;
        }

        _last = counters;
        _lastAt = at;
    }

    public void Reset()
    {
        _samples.Clear();
        _last = null;
        Current = default;
    }

    /// <summary>Верх шкалы графика: ближайшее сверху «круглое» значение (1, 2, 5 × 10ⁿ КБ/с), не меньше 100 КБ/с.</summary>
    public static double NiceMaximum(IEnumerable<TrafficSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var peak = samples.Select(s => Math.Max(s.UpPerSecond, s.DownPerSecond)).DefaultIfEmpty(0).Max() / 1024;
        for (var decade = 100.0; ; decade *= 10)
        {
            foreach (var step in s_steps)
            {
                if (step * decade >= peak)
                {
                    return step * decade * 1024;
                }
            }
        }
    }
}

using Khors.Core.Diagnostics;

namespace Khors.Engines.Processes;

/// <summary>
/// Последние строки лога ядра. Каждая строка маскируется при поступлении, поэтому
/// незамаскированный текст ядра нигде не хранится и дальше не передаётся.
/// </summary>
public sealed class CoreLogBuffer
{
    private readonly SecretMasker _masker;
    private readonly int _capacity;
    private readonly Queue<CoreLogLine> _lines = new();
    private readonly Lock _lock = new();

    public CoreLogBuffer(SecretMasker masker, int capacity = 1000)
    {
        ArgumentNullException.ThrowIfNull(masker);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _masker = masker;
        _capacity = capacity;
    }

    /// <summary>Новая строка (уже замаскированная). Вызывается в потоке чтения лога.</summary>
    public event EventHandler<CoreLogLine>? LineAdded;

    public CoreLogLine Add(CoreLogSource source, string rawText)
    {
        var line = new CoreLogLine(DateTimeOffset.Now, source, _masker.MaskText(rawText));
        lock (_lock)
        {
            if (_lines.Count == _capacity)
            {
                _lines.Dequeue();
            }

            _lines.Enqueue(line);
        }

        LineAdded?.Invoke(this, line);
        return line;
    }

    public IReadOnlyList<CoreLogLine> Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    public IReadOnlyList<string> Tail(int count)
    {
        lock (_lock)
        {
            return [.. _lines.Skip(Math.Max(0, _lines.Count - count)).Select(l => l.Text)];
        }
    }
}

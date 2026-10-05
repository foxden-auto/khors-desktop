using System.ComponentModel;
using System.Diagnostics;

namespace Khors.Engines;

/// <summary>
/// Держит процесс сторожа запущенным: если его завершили (например, по ошибке в Диспетчере задач),
/// запускает заново. Защита от зацикливания: не больше <c>maxRestarts</c> перезапусков за <c>window</c>;
/// после этого перезапуски прекращаются до следующего явного <see cref="EnsureStarted"/>.
/// </summary>
internal sealed class WatchdogKeeper : IDisposable
{
    private readonly Func<ProcessStartInfo> _startInfo;
    private readonly TimeProvider _time;
    private readonly int _maxRestarts;
    private readonly TimeSpan _window;
    private readonly Lock _lock = new();
    private readonly Queue<DateTimeOffset> _restarts = new();
    private Process? _current;
    private bool _disposed;

    public WatchdogKeeper(Func<ProcessStartInfo> startInfo, int maxRestarts = 5, TimeSpan? window = null, TimeProvider? time = null)
    {
        _startInfo = startInfo;
        _maxRestarts = maxRestarts;
        _window = window ?? TimeSpan.FromMinutes(1);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Сколько раз процесс сторожа запускался (для тестов и диагностики).</summary>
    public int Starts { get; private set; }

    /// <summary>Перезапуски прекращены: сторож завершался слишком часто.</summary>
    public bool GaveUp { get; private set; }

    public int? ProcessId
    {
        get
        {
            lock (_lock)
            {
                return _current is { HasExited: false } process ? process.Id : null;
            }
        }
    }

    /// <summary>Запускает сторожа, если он не работает. Сбрасывает отказ от перезапусков.</summary>
    public void EnsureStarted()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            GaveUp = false;
            _restarts.Clear();
            if (_current is { HasExited: false })
            {
                return;
            }

            Start();
        }
    }

    /// <summary>Перестаёт следить. Сам сторож не завершается: он нужен, пока жив процесс KHORS.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            if (_current is not null)
            {
                _current.Exited -= OnExited;
                _current.Dispose();
                _current = null;
            }
        }
    }

    private void Start()
    {
        if (_current is not null)
        {
            _current.Exited -= OnExited;
            _current.Dispose();
        }

        var process = new Process { StartInfo = _startInfo(), EnableRaisingEvents = true };
        process.Exited += OnExited;
        process.Start();
        _current = process;
        Starts++;
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (_lock)
        {
            if (_disposed || !ReferenceEquals(sender, _current) || GaveUp)
            {
                return;
            }

            var now = _time.GetUtcNow();
            while (_restarts.Count > 0 && now - _restarts.Peek() > _window)
            {
                _restarts.Dequeue();
            }

            if (_restarts.Count >= _maxRestarts)
            {
                GaveUp = true;
                return;
            }

            _restarts.Enqueue(now);
            try
            {
                Start();
            }
            catch (Win32Exception)
            {
                GaveUp = true;
            }
        }
    }
}

using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>
/// Смена сети и пробуждение (ROADMAP 4.3). Сеть появилась или сменилась — ждущая попытка переподключения
/// выполняется сразу (<see cref="ConnectionManager.RetryNow"/>). Если в этот момент или после сна подключено —
/// подключение проверяется через работающее ядро (<see cref="HealthCheck"/>); две неудачные проверки подряд —
/// перезапуск (<see cref="ConnectionManager.RestartAsync"/>). Ядра обычно сами переходят на новую сеть, поэтому
/// перезапуск — только если проверка не прошла.
/// </summary>
public sealed class ConnectionWatchdog : IDisposable
{
    /// <summary>Пауза после события, чтобы сеть успела подняться (адрес, DNS, маршруты).</summary>
    public static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);

    /// <summary>Пауза перед второй проверкой.</summary>
    public static readonly TimeSpan RecheckDelay = TimeSpan.FromSeconds(5);

    /// <summary>Сколько после пробуждения ждать сеть, прежде чем проверять.</summary>
    public static readonly TimeSpan ResumeNetworkWait = TimeSpan.FromSeconds(30);

    private readonly ConnectionManager _connection;
    private readonly INetworkMonitor? _network;
    private readonly IPowerEvents? _power;
    private readonly Func<CancellationToken, Task<bool>> _healthCheck;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _check;
    private int _disposed;

    /// <param name="healthCheck">Запрос через работающее ядро: <c>true</c> — проходит.</param>
    /// <param name="delay">Пауза (для тестов — подмена).</param>
    public ConnectionWatchdog(
        ConnectionManager connection,
        INetworkMonitor? network,
        IPowerEvents? power,
        Func<CancellationToken, Task<bool>> healthCheck,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(healthCheck);
        _connection = connection;
        _network = network;
        _power = power;
        _healthCheck = healthCheck;
        _delay = delay ?? Task.Delay;

        if (network is not null)
        {
            connection.IsNetworkAvailable = () => network.IsAvailable;
            network.Changed += OnNetworkChanged;
        }

        if (power is not null)
        {
            power.Resumed += OnResumed;
        }
    }

    /// <summary>Идущая проверка (для тестов).</summary>
    internal Task Checking { get; private set; } = Task.CompletedTask;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_network is not null)
        {
            _network.Changed -= OnNetworkChanged;
        }

        if (_power is not null)
        {
            _power.Resumed -= OnResumed;
        }

        lock (_lock)
        {
            _check?.Cancel();
            _check?.Dispose();
            _check = null;
        }
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        if (_network?.IsAvailable != false)
        {
            _connection.RetryNow();
            StartCheck(waitForNetwork: false);
        }
        else
        {
            // Попытка, ждущая паузу, должна узнать, что сети больше нет, и ждать её.
            _connection.RetryNow();
        }
    }

    private void OnResumed(object? sender, EventArgs e)
    {
        _connection.RetryNow();
        StartCheck(waitForNetwork: true);
    }

    // Новое событие отменяет прежнюю проверку: проверяем уже новую сеть.
    private void StartCheck(bool waitForNetwork)
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        CancellationTokenSource cancellation;
        lock (_lock)
        {
            _check?.Cancel();
            _check?.Dispose();
            cancellation = new CancellationTokenSource();
            _check = cancellation;
        }

        Checking = CheckAsync(waitForNetwork, cancellation.Token);
    }

    private async Task CheckAsync(bool waitForNetwork, CancellationToken cancellationToken)
    {
        try
        {
            await _delay(SettleDelay, cancellationToken).ConfigureAwait(false);
            if (waitForNetwork)
            {
                var waited = SettleDelay;
                while (_network?.IsAvailable == false && waited < ResumeNetworkWait)
                {
                    await _delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    waited += TimeSpan.FromSeconds(1);
                }
            }

            for (var check = 0; check < 2; check++)
            {
                if (_connection.Status.State != ConnectionState.Connected || _network?.IsAvailable == false)
                {
                    return;
                }

                if (await _healthCheck(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (check == 0)
                {
                    await _delay(RecheckDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            await _connection.RestartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Пришло новое событие или окно закрывается.
        }
    }
}

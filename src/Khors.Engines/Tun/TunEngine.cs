using System.Net;
using System.Net.Sockets;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Generators.SingBox;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Engines.Traffic;
using Khors.Platform;

namespace Khors.Engines.Tun;

/// <summary>Работающий режим TUN: одно или два ядра, их общий лог и завершение.</summary>
public interface ITunRun : IAsyncDisposable
{
    /// <summary>Ядро профиля: sing-box — всё в одном процессе, Xray — цепочка TUN(sing-box) → SOCKS → Xray.</summary>
    CoreKind Core { get; }

    /// <summary>Локальные входы sing-box на 127.0.0.1 (тест задержки из окна).</summary>
    int SocksPort { get; }

    int HttpPort { get; }

    /// <summary>Строка лога любого из ядер (уже замаскирована). Приходит в потоке чтения лога.</summary>
    event EventHandler<CoreLogLine>? LineAdded;

    /// <summary>Завершается, когда завершилось любое из ядер (второе тогда останавливается).</summary>
    Task<CoreExit> Completion { get; }

    IReadOnlyList<string> Tail(int count);

    /// <summary>Трафик через сервер: у цепочки — счётчики Xray, иначе — sing-box.</summary>
    Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) => Task.FromResult<TrafficCounters?>(null);
}

/// <summary>Запуск режима TUN (для тестов службы — подмена).</summary>
public interface ITunStarter
{
    /// <exception cref="CoreStartException">Ядро не запустилось или профиль не поддерживается.</exception>
    Task<ITunRun> StartAsync(Profile profile, string logLevel, CancellationToken cancellationToken);
}

/// <summary>
/// Режим TUN в службе (docs/SPEC.md, 3.3). Профиль, который запускает sing-box, — один процесс с адаптером TUN.
/// Профиль для Xray (REALITY с X25519MLKEM768, XHTTP и т.п.) — цепочка: Xray с локальным SOCKS-входом и sing-box,
/// чей выход — этот вход. Адрес сервера разрешается до подъёма TUN и подставляется в профиль Xray как IP
/// (<see cref="ProfileAddress.WithResolvedHost"/>), а сам IP исключается из туннеля — иначе соединения Xray
/// с сервером снова попали бы в TUN. Исполняемые файлы ядер — только из каталога службы (CLAUDE.md, правило 8).
/// </summary>
public sealed class TunEngine(SecretMasker masker, IChildProcessGuard? guard) : ITunStarter
{
    private static readonly TimeSpan s_resolveTimeout = TimeSpan.FromSeconds(10);

    // sing-box на Windows ждёт открытия адаптера до ~15 с и только потом пишет причину («configure tun interface: …»).
    // Ожидание короче убивало бы ядро молча: в логе пусто, а прерванное создание может оставить битый адаптер.
    private static readonly TimeSpan s_tunReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Разрешение имени сервера (для тестов — подмена).</summary>
    public Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = Dns.GetHostAddressesAsync;

    public async Task<ITunRun> StartAsync(Profile profile, string logLevel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var choice = CoreSelection.Select(profile);
        if (choice.Unsupported is { } field)
        {
            throw new CoreStartException(CoreStartFailure.ConfigNotGenerated, $"{choice.Core} cannot run the profile: {field}.", field: field)
            {
                Core = choice.Core,
                ConfigError = new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, field),
            };
        }

        var options = new CoreStartOptions { PreferredSocksPort = null, PreferredHttpPort = null, LogLevel = logLevel };
        if (choice.Core == CoreKind.SingBox)
        {
            var singBox = await StartSingBoxAsync(profile, options with { Tun = new SingBoxTunOptions(), TrafficStats = true, ReadyTimeout = s_tunReadyTimeout }, cancellationToken).ConfigureAwait(false);
            return new TunRun(CoreKind.SingBox, singBox, chained: null);
        }

        var server = await ResolveServerAsync(profile, cancellationToken).ConfigureAwait(false);
        var resolved = ProfileAddress.WithResolvedHost(profile, server);
        var xray = await CoreLauncher.StartAsync(CoreKind.Xray, resolved, options with { TrafficStats = true }, masker, guard, cancellationToken).ConfigureAwait(false);
        try
        {
            var tunOptions = options with
            {
                Tun = new SingBoxTunOptions { ExcludeAddresses = [server.ToString()] },
                UpstreamSocksPort = xray.SocksPort,
                ReadyTimeout = s_tunReadyTimeout,
            };
            var singBox = await StartSingBoxAsync(resolved, tunOptions, cancellationToken).ConfigureAwait(false);
            return new TunRun(CoreKind.Xray, singBox, xray);
        }
        catch
        {
            await xray.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// sing-box с адаптером TUN. Если в системе остался битый адаптер KHORS (создать нельзя — «уже существует»,
    /// открыть тоже нельзя), первая попытка падает через ~15 с, а повторная проходит: неудачная попытка
    /// его убирает (живые проверки 2026-10-06). Поэтому при этой ошибке — один повтор. Откуда берётся битый
    /// адаптер, не установлено (ROADMAP, «Известные проблемы»).
    /// </summary>
    private async Task<CoreSession> StartSingBoxAsync(Profile profile, CoreStartOptions options, CancellationToken cancellationToken)
    {
        try
        {
            return await CoreLauncher.StartAsync(CoreKind.SingBox, profile, options, masker, guard, cancellationToken).ConfigureAwait(false);
        }
        catch (CoreStartException ex) when (IsStaleAdapter(ex.LogTail))
        {
            return await CoreLauncher.StartAsync(CoreKind.SingBox, profile, options, masker, guard, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>sing-box не смог ни создать адаптер (имя занято), ни открыть существующий.</summary>
    internal static bool IsStaleAdapter(IEnumerable<string> logTail) =>
        logTail.Any(line => line.Contains("create adapter", StringComparison.OrdinalIgnoreCase)
            && line.Contains("open existing adapter", StringComparison.OrdinalIgnoreCase));

    private async Task<IPAddress> ResolveServerAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(profile.Server.Host, out var literal))
        {
            return literal;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(s_resolveTimeout);
        IPAddress[] addresses;
        try
        {
            addresses = await Resolve(profile.Server.Host, limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            addresses = [];
        }

        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new CoreStartException(CoreStartFailure.ExitedDuringStart, "Server name was not resolved.")
            {
                Core = CoreKind.Xray,
                Diagnosis = new CoreDiagnosis(CoreProblem.ServerNotFound, CoreKind.Xray),
            };
    }

    private sealed class TunRun : ITunRun
    {
        private readonly CoreSession _singBox;
        private readonly CoreSession? _chained;
        private readonly TaskCompletionSource<CoreExit> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _stopping;

        public TunRun(CoreKind core, CoreSession singBox, CoreSession? chained)
        {
            Core = core;
            _singBox = singBox;
            _chained = chained;
            foreach (var session in Sessions)
            {
                session.Process.Log.LineAdded += OnLine;
                _ = WatchAsync(session);
            }
        }

        public event EventHandler<CoreLogLine>? LineAdded;

        public CoreKind Core { get; }

        public int SocksPort => _singBox.SocksPort;

        public int HttpPort => _singBox.HttpPort;

        public Task<CoreExit> Completion => _completion.Task;

        private IEnumerable<CoreSession> Sessions => _chained is null ? [_singBox] : [_chained, _singBox];

        public Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) =>
            (_chained ?? _singBox).ReadTrafficAsync(cancellationToken);

        public IReadOnlyList<string> Tail(int count) =>
            [.. Sessions.SelectMany(s => s.Process.Log.Snapshot()).OrderBy(l => l.Time).TakeLast(count).Select(l => l.Text)];

        public async ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stopping, 1);

            // Сначала sing-box: адаптер и маршруты снимаются, пока Xray ещё работает.
            await _singBox.DisposeAsync().ConfigureAwait(false);
            if (_chained is not null)
            {
                await _chained.DisposeAsync().ConfigureAwait(false);
            }

            foreach (var session in Sessions)
            {
                session.Process.Log.LineAdded -= OnLine;
            }

            _completion.TrySetResult(new CoreExit(0, Expected: true, DateTimeOffset.Now));
        }

        private void OnLine(object? sender, CoreLogLine line) => LineAdded?.Invoke(this, line);

        // Падение любого ядра — конец режима: второе ядро без первого бесполезно (а sing-box без Xray держал бы TUN без выхода).
        private async Task WatchAsync(CoreSession session)
        {
            var exit = await session.Process.Completion.ConfigureAwait(false);
            if (exit.Expected || Volatile.Read(ref _stopping) == 1)
            {
                return;
            }

            await DisposeOthersAsync(session).ConfigureAwait(false);
            _completion.TrySetResult(exit);
        }

        private async Task DisposeOthersAsync(CoreSession crashed)
        {
            Interlocked.Exchange(ref _stopping, 1);
            foreach (var session in Sessions.Where(s => s != crashed))
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

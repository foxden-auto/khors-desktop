using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Generators.SingBox;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Engines.Traffic;
using Khors.Platform;

namespace Khors.Engines;

public sealed record CoreStartOptions
{
    /// <summary>Желаемые порты; если заняты — берутся свободные. <c>null</c> — любой свободный.</summary>
    public int? PreferredSocksPort { get; init; } = 10808;

    public int? PreferredHttpPort { get; init; } = 10809;

    /// <summary>Счётчики трафика на loopback (<see cref="CoreSession.Traffic"/>): Xray — metrics, sing-box — Clash API.</summary>
    public bool TrafficStats { get; init; }

    /// <summary>Уровень лога в терминах Xray (debug, info, warning, error, none); как он применяется — <see cref="CoreLogLevels.Plan"/>.</summary>
    public string LogLevel { get; init; } = "warning";

    /// <summary>Путь к своей сборке ядра (docs/SPEC.md, 4.8); <c>null</c> — поставляемая.</summary>
    public string? ExecutablePath { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>sing-box в режиме TUN (только служба); <c>null</c> — только локальные входы.</summary>
    public SingBoxTunOptions? Tun { get; init; }

    /// <summary>sing-box в цепочке: выход — SOCKS-вход Xray на этом порту (ROADMAP 3.3).</summary>
    public int? UpstreamSocksPort { get; init; }
}

/// <summary>
/// Запуск ядра для профиля: порты → конфиг из модели → процесс (конфиг через stdin — секреты не пишутся на диск)
/// → ожидание готовности. Ядро выбирает вызывающий код (<see cref="Connection.CoreSelection"/>).
/// </summary>
public static class CoreLauncher
{
    private static readonly TimeSpan s_resolveTimeout = TimeSpan.FromSeconds(5);

    public static async Task<CoreSession> StartAsync(
        CoreKind kind,
        Profile profile,
        CoreStartOptions options,
        SecretMasker masker,
        IChildProcessGuard? guard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(masker);

        var executable = CoreLocator.Find(kind, options.ExecutablePath)
            ?? throw new CoreStartException(CoreStartFailure.ExecutableNotFound, $"{kind} executable not found.") { Core = kind };

        if (kind == CoreKind.SingBox && options.UpstreamSocksPort is null)
        {
            profile = await ResolveWireGuardServerAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        var ports = options.TrafficStats
            ? PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort, null)
            : PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort);
        var traffic = options.TrafficStats
            ? new TrafficEndpoint(kind, ports[2], kind == CoreKind.SingBox ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) : null)
            : null;

        var log = CoreLogLevels.Plan(kind, options.LogLevel);
        var config = kind switch
        {
            CoreKind.Xray => XrayConfigGenerator.Generate(profile, new XrayConfigOptions
            {
                SocksPort = ports[0],
                HttpPort = ports[1],
                MetricsPort = traffic?.Port,
                LogLevel = log.Level,
            }),
            CoreKind.SingBox => SingBoxConfigGenerator.Generate(profile, new SingBoxConfigOptions
            {
                SocksPort = ports[0],
                HttpPort = ports[1],
                LogLevel = log.Level,
                Tun = options.Tun,
                UpstreamSocksPort = options.UpstreamSocksPort,
                ClashApi = traffic is { Secret: { } secret } ? new ClashApiOptions(traffic.Port, secret) : null,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        if (!config.IsSuccess)
        {
            throw new CoreStartException(CoreStartFailure.ConfigNotGenerated, $"{kind} config not generated: {config.Error.Code}.", field: config.Error.Field)
            {
                ConfigError = config.Error,
                Core = kind,
            };
        }

        string[] arguments = kind == CoreKind.Xray ? ["run", "-c", "stdin:"] : ["run", "-c", "stdin", "--disable-color"];
        var launch = new CoreLaunch(executable, arguments, config.Json, new IPEndPoint(IPAddress.Loopback, ports[0]), options.ReadyTimeout)
        {
            KeepLine = log.KeepLine,
        };

        try
        {
            var process = await CoreProcess.StartAsync(launch, new CoreLogBuffer(masker), guard, cancellationToken).ConfigureAwait(false);
            return new CoreSession(kind, process, ports[0], ports[1], traffic);
        }
        catch (CoreStartException ex) when (ex.Core is null)
        {
            throw new CoreStartException(ex.Failure, ex.Message, ex.ExitCode, ex.LogTail, ex.Field) { ConfigError = ex.ConfigError, Core = kind };
        }
    }

    /// <summary>
    /// Адрес сервера WireGuard, заданный доменом, разрешается системным резолвером до запуска ядра.
    /// Свой резолвер sing-box («local») шлёт UDP-запросы на DNS из настроек адаптера маршрутом по умолчанию,
    /// то есть в сам туннель WireGuard, который без адреса сервера не поднимается: замкнутый круг
    /// («no known endpoint for peer»), а соединения висят. У WireGuard нет TLS, поэтому IP вместо имени ничего не меняет.
    /// Если имя не разрешилось — остаётся как есть, sing-box попробует сам.
    /// </summary>
    internal static async Task<Profile> ResolveWireGuardServerAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.Protocol is not WireGuardSettings || IPAddress.TryParse(profile.Server.Host, out _))
        {
            return profile;
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(s_resolveTimeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(profile.Server.Host, limit.Token).ConfigureAwait(false);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
            return address is null ? profile : profile with { Server = profile.Server with { Host = address.ToString() } };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return profile;
        }
        catch (SocketException)
        {
            return profile;
        }
    }
}

/// <summary>Запущенное ядро и его локальные входы.</summary>
public sealed class CoreSession(CoreKind core, CoreProcess process, int socksPort, int httpPort, TrafficEndpoint? traffic = null) : IAsyncDisposable
{
    public CoreKind Core { get; } = core;

    public CoreProcess Process { get; } = process;

    public int SocksPort { get; } = socksPort;

    public int HttpPort { get; } = httpPort;

    /// <summary>Где читать счётчики трафика; <c>null</c> — запущено без них.</summary>
    public TrafficEndpoint? Traffic { get; } = traffic;

    /// <summary>Счётчики с запуска ядра; <c>null</c> — выключены или ядро не ответило.</summary>
    public Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) =>
        Traffic is { } endpoint ? TrafficReader.ReadAsync(endpoint, cancellationToken) : Task.FromResult<TrafficCounters?>(null);

    public Task StopAsync(CancellationToken cancellationToken = default) => Process.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => Process.DisposeAsync();
}

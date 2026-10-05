using System.Net;
using System.Net.Sockets;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Generators.SingBox;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines;

public sealed record CoreStartOptions
{
    /// <summary>Желаемые порты; если заняты — берутся свободные. <c>null</c> — любой свободный.</summary>
    public int? PreferredSocksPort { get; init; } = 10808;

    public int? PreferredHttpPort { get; init; } = 10809;

    /// <summary>API статистики (только Xray).</summary>
    public bool EnableStatsApi { get; init; }

    /// <summary>Уровень лога в терминах Xray (debug, info, warning, error, none).</summary>
    public string LogLevel { get; init; } = "warning";

    /// <summary>Путь к своей сборке ядра (docs/SPEC.md, 4.8); <c>null</c> — поставляемая.</summary>
    public string? ExecutablePath { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);
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

        if (kind == CoreKind.SingBox)
        {
            profile = await ResolveWireGuardServerAsync(profile, cancellationToken).ConfigureAwait(false);
        }

        var withApi = kind == CoreKind.Xray && options.EnableStatsApi;
        var ports = withApi
            ? PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort, null)
            : PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort);

        var config = kind switch
        {
            CoreKind.Xray => XrayConfigGenerator.Generate(profile, new XrayConfigOptions
            {
                SocksPort = ports[0],
                HttpPort = ports[1],
                ApiPort = withApi ? ports[2] : null,
                LogLevel = options.LogLevel,
            }),
            CoreKind.SingBox => SingBoxConfigGenerator.Generate(profile, new SingBoxConfigOptions
            {
                SocksPort = ports[0],
                HttpPort = ports[1],
                LogLevel = options.LogLevel,
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
        var launch = new CoreLaunch(executable, arguments, config.Json, new IPEndPoint(IPAddress.Loopback, ports[0]), options.ReadyTimeout);

        try
        {
            var process = await CoreProcess.StartAsync(launch, new CoreLogBuffer(masker), guard, cancellationToken).ConfigureAwait(false);
            return new CoreSession(kind, process, ports[0], ports[1], withApi ? ports[2] : null);
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
public sealed class CoreSession(CoreKind core, CoreProcess process, int socksPort, int httpPort, int? apiPort) : IAsyncDisposable
{
    public CoreKind Core { get; } = core;

    public CoreProcess Process { get; } = process;

    public int SocksPort { get; } = socksPort;

    public int HttpPort { get; } = httpPort;

    public int? ApiPort { get; } = apiPort;

    public Task StopAsync(CancellationToken cancellationToken = default) => Process.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => Process.DisposeAsync();
}

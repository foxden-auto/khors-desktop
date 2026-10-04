using System.Net;
using Khors.Core.Diagnostics;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines.Xray;

public sealed record XrayStartOptions
{
    /// <summary>Желаемые порты; если заняты — берутся свободные. <c>null</c> — любой свободный.</summary>
    public int? PreferredSocksPort { get; init; } = 10808;

    public int? PreferredHttpPort { get; init; } = 10809;

    public bool EnableStatsApi { get; init; }

    public string LogLevel { get; init; } = "warning";

    /// <summary>Путь к своей сборке Xray (docs/SPEC.md, 4.8); <c>null</c> — поставляемая.</summary>
    public string? ExecutablePath { get; init; }

    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>Запуск Xray для профиля: порты → конфиг (через stdin) → процесс → ожидание готовности.</summary>
public static class XrayLauncher
{
    public static async Task<XraySession> StartAsync(
        Profile profile,
        XrayStartOptions options,
        SecretMasker masker,
        IChildProcessGuard? guard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(masker);

        var executable = CoreLocator.Find(CoreKind.Xray, options.ExecutablePath)
            ?? throw new CoreStartException(CoreStartFailure.ExecutableNotFound, "Xray executable not found.");

        var ports = options.EnableStatsApi
            ? PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort, null)
            : PortAllocator.Allocate(options.PreferredSocksPort, options.PreferredHttpPort);

        var config = XrayConfigGenerator.Generate(profile, new XrayConfigOptions
        {
            SocksPort = ports[0],
            HttpPort = ports[1],
            ApiPort = options.EnableStatsApi ? ports[2] : null,
            LogLevel = options.LogLevel,
        });

        if (!config.IsSuccess)
        {
            throw new CoreStartException(CoreStartFailure.ConfigNotGenerated, $"Xray config not generated: {config.Error.Code}.", field: config.Error.Field)
            {
                ConfigError = config.Error,
            };
        }

        var launch = new CoreLaunch(
            executable,
            ["run", "-c", "stdin:"],
            config.Json,
            new IPEndPoint(IPAddress.Loopback, ports[0]),
            options.ReadyTimeout);

        var process = await CoreProcess.StartAsync(launch, new CoreLogBuffer(masker), guard, cancellationToken).ConfigureAwait(false);
        return new XraySession(process, ports[0], ports[1], options.EnableStatsApi ? ports[2] : null);
    }
}

/// <summary>Запущенный Xray и его локальные входы.</summary>
public sealed class XraySession(CoreProcess process, int socksPort, int httpPort, int? apiPort) : IAsyncDisposable
{
    public CoreProcess Process { get; } = process;

    public int SocksPort { get; } = socksPort;

    public int HttpPort { get; } = httpPort;

    public int? ApiPort { get; } = apiPort;

    public Task StopAsync(CancellationToken cancellationToken = default) => Process.StopAsync(cancellationToken);

    public ValueTask DisposeAsync() => Process.DisposeAsync();
}

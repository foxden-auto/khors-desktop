using Khors.Core.Profiles;
using Khors.Core.Routing;
using Khors.Core.Storage;
using Khors.Engines.Processes;
using Khors.Engines.Traffic;

namespace Khors.Engines.Connection;

/// <summary>Запущенное ядро с локальными входами.</summary>
public interface ICoreSession : IAsyncDisposable
{
    CoreKind Core { get; }

    int SocksPort { get; }

    int HttpPort { get; }

    CoreLogBuffer Log { get; }

    /// <summary>Завершается, когда процесс ядра завершился.</summary>
    Task<CoreExit> Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>Счётчики трафика с запуска; <c>null</c> — ядро запущено без них или не ответило.</summary>
    Task<TrafficCounters?> ReadTrafficAsync(CancellationToken cancellationToken = default) => Task.FromResult<TrafficCounters?>(null);

    /// <summary>
    /// Категории гео-баз из правил маршрутизации, которых нет в базе (<c>geosite:код</c>, <c>geoip:код</c>):
    /// правила с ними не действуют, остальные работают.
    /// </summary>
    IReadOnlyList<string> MissingGeo => [];
}

/// <summary>Запуск ядра для профиля. Ошибки запуска — <see cref="CoreStartException"/>.</summary>
public interface ICoreLauncher
{
    Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken);
}

/// <summary>Пожелания к запуску из настроек пользователя.</summary>
/// <param name="Mode">TUN — ядра запускает служба, системный прокси не меняется.</param>
/// <param name="TrafficStats">Счётчики трафика (для подключения; временным ядрам теста задержки не нужны).</param>
/// <param name="RemoteDns">Удалённый DNS из настроек (текст); пустой или неверный — по умолчанию.</param>
/// <param name="Routing">Маршрутизация из настроек (ROADMAP 3.5); <c>null</c> — по умолчанию (временные ядра теста задержки).</param>
public sealed record CoreStartPreferences(
    int? SocksPort = 10808,
    int? HttpPort = 10809,
    string LogLevel = "warning",
    ConnectionMode Mode = ConnectionMode.SystemProxy,
    bool TrafficStats = false,
    string? RemoteDns = null,
    RoutingSettings? Routing = null)
{
    public static CoreStartPreferences From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CoreStartPreferences(settings.SocksPort, settings.HttpPort, settings.CoreLogLevel, settings.ConnectionMode, TrafficStats: true, settings.RemoteDns, settings.Routing);
    }
}

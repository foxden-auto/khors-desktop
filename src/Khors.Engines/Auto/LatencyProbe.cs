using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Latency;

namespace Khors.Engines.Auto;

/// <summary>Замер задержки профиля для группы «Авто».</summary>
public interface ILatencyProbe
{
    Task<LatencyResult> MeasureAsync(Profile profile, CancellationToken cancellationToken);
}

/// <summary>
/// Подключённый профиль — через уже работающее ядро, остальные — временным ядром (системный прокси не меняется).
/// </summary>
public sealed class LatencyProbe(ConnectionManager connection, ICoreLauncher launcher, Func<Uri> url, TimeSpan timeout) : ILatencyProbe
{
    public Task<LatencyResult> MeasureAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return connection.Status is { State: ConnectionState.Connected, Profile: { } current, HttpPort: { } port } && current.Id == profile.Id
            ? LatencyTester.MeasureThroughProxyAsync(port, url(), timeout, cancellationToken)
            : LatencyTester.MeasureProfileAsync(profile, launcher, url(), timeout, cancellationToken);
    }
}

using Khors.Engines.Geo;
using Khors.Ipc;

namespace Khors.Service.Geo;

/// <summary>
/// Гео-базы службы (ROADMAP 3.6): своя копия в каталоге службы — режим TUN не берёт файлы из окна (CLAUDE.md, правило 8).
/// Обновляются по команде окна (кнопка и расписание окна); загрузка напрямую, без локального прокси окна.
/// </summary>
public sealed class ServiceGeo(GeoDatabaseUpdater updater)
{
    private readonly Dictionary<GeoDatabaseKind, GeoUpdateError> _errors = [];
    private readonly Lock _lock = new();

    public GeoStatusResponse Status()
    {
        lock (_lock)
        {
            return new GeoStatusResponse([.. updater.GetStatus().Select(s => new IpcGeoFile(
                ToIpc(s.Kind),
                s.Size,
                s.Updated,
                _errors.TryGetValue(s.Kind, out var error) ? error.ToString() : null))]);
        }
    }

    public async Task<GeoStatusResponse> UpdateAsync(CancellationToken cancellationToken)
    {
        var outcomes = await updater.UpdateAsync(cancellationToken).ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var outcome in outcomes)
            {
                if (outcome.Error is { } error)
                {
                    _errors[outcome.Kind] = error;
                }
                else
                {
                    _errors.Remove(outcome.Kind);
                }
            }
        }

        return Status();
    }

    private static IpcGeoKind ToIpc(GeoDatabaseKind kind) => kind == GeoDatabaseKind.Site ? IpcGeoKind.Site : IpcGeoKind.Ip;
}

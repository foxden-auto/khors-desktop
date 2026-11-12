using Khors.Engines;
using Khors.Engines.Cores;
using Khors.Ipc;

namespace Khors.Service.Cores;

/// <summary>
/// Ядра службы (ROADMAP 5.2): своё хранилище в каталоге службы, куда пишет только администратор; файлы окна не берутся
/// (CLAUDE.md, правило 8). Окно называет только версию — релиз, архив и контрольную сумму служба проверяет сама,
/// загрузка — напрямую. Новая версия действует со следующего запуска TUN.
/// </summary>
public sealed class ServiceCores(CoreUpdater updater)
{
    public CoresStatusResponse Status(string? error = null, string? detail = null) =>
        new([.. updater.Status().Select(s => new IpcCoreState(ToIpc(s.Kind), s.Version, s.Shipped, s.PreviousVersion))], error, detail);

    public async Task<CoresStatusResponse> UpdateAsync(IpcCore core, string version, CancellationToken cancellationToken)
    {
        var outcome = await updater.InstallVersionAsync(FromIpc(core), version, cancellationToken).ConfigureAwait(false);
        return Status(outcome.Error?.ToString(), outcome.Detail);
    }

    public CoresStatusResponse Rollback(IpcCore core) =>
        updater.Rollback(FromIpc(core)) ? Status() : Status(error: "NothingToRollback");

    private static IpcCore ToIpc(CoreKind kind) => kind switch
    {
        CoreKind.Xray => IpcCore.Xray,
        CoreKind.SingBox => IpcCore.SingBox,
        _ => IpcCore.SingBoxExtended,
    };

    private static CoreKind FromIpc(IpcCore core) => core switch
    {
        IpcCore.Xray => CoreKind.Xray,
        IpcCore.SingBox => CoreKind.SingBox,
        _ => CoreKind.SingBoxExtended,
    };
}

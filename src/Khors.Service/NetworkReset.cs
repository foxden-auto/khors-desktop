using Khors.Ipc;
using Khors.Platform;
using Khors.Service.KillSwitch;
using Khors.Service.Tun;

namespace Khors.Service;

/// <summary>
/// «Сбросить сетевые настройки KHORS» (ROADMAP 4.4, docs/SPEC.md, 4.6): выключить TUN, снять фильтры WFP и выключить
/// Kill Switch, удалить оставшийся адаптер KHORS. Шаги независимы: сбой одного не мешает следующим. Работает и в службе
/// (команда IPC), и в <c>khors-service.exe reset</c> с правами администратора, когда службы нет.
/// </summary>
/// <param name="tun"><c>null</c> — вне процесса службы: TUN некому выключать.</param>
/// <param name="adapters"><c>null</c> — платформа без удаления адаптера.</param>
public sealed class NetworkReset(TunController? tun, KillSwitchController killSwitch, ITunAdapterCleanup? adapters, string tunnelInterface = KillSwitchController.TunnelInterface)
{
    public async Task<NetworkResetResponse> RunAsync()
    {
        var items = new List<IpcResetItem>();
        if (tun is not null)
        {
            items.Add(await StepAsync(IpcResetStep.Tunnel, async () => await tun.ResetAsync().ConfigureAwait(false)).ConfigureAwait(false));
        }

        // Фильтры — после TUN: пока туннель работал, блокировка «пока подключено» должна была стоять.
        var removed = killSwitch.Reset();
        items.Add(new IpcResetItem(
            IpcResetStep.KillSwitch,
            removed switch
            {
                null => IpcResetOutcome.NothingToDo,
                true => IpcResetOutcome.Done,
                false => IpcResetOutcome.Failed,
            },
            removed == false ? killSwitch.Status().Error : null));

        // Адаптер — после остановки sing-box, иначе удалили бы работающий.
        if (adapters is not null)
        {
            items.Add(await StepAsync(IpcResetStep.Adapter, () => Task.FromResult(adapters.RemoveAdapters(tunnelInterface) > 0)).ConfigureAwait(false));
        }

        return new NetworkResetResponse(items);
    }

    private static async Task<IpcResetItem> StepAsync(IpcResetStep step, Func<Task<bool>> action)
    {
        try
        {
            return new IpcResetItem(step, await action().ConfigureAwait(false) ? IpcResetOutcome.Done : IpcResetOutcome.NothingToDo);
        }
#pragma warning disable CA1031 // Сбой шага — в итог, следующие шаги выполняются.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new IpcResetItem(step, IpcResetOutcome.Failed, ex.Message);
        }
    }
}

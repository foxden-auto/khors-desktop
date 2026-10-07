using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Ipc;
using Khors.Platform;

namespace Khors.App.ViewModels;

/// <summary>
/// «Сбросить сетевые настройки KHORS» (ROADMAP 4.4): отключение, системный прокси (окно), TUN, фильтры WFP, Kill Switch
/// и адаптер (служба; без службы — <c>khors-service.exe reset</c> с правами администратора).
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Показано подтверждение со списком того, что будет сделано.</summary>
    [ObservableProperty]
    public partial bool IsResetConfirming { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetNetworkCommand))]
    [NotifyCanExecuteChangedFor(nameof(AskResetNetworkCommand))]
    public partial bool IsResetting { get; set; }

    /// <summary>Итог последнего сброса по пунктам; <c>null</c> — сброса не было.</summary>
    [ObservableProperty]
    public partial string? ResetResult { get; set; }

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void AskResetNetwork()
    {
        ResetResult = null;
        IsResetConfirming = true;
    }

    [RelayCommand]
    private void CancelResetNetwork() => IsResetConfirming = false;

    private bool CanReset() => !IsResetting;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetNetworkAsync()
    {
        IsResetConfirming = false;
        IsResetting = true;
        var lines = new List<string>();
        try
        {
            // Сначала отключение: «Авто» и попытки переподключения не должны поднять подключение заново.
            if (_auto.IsActive)
            {
                await _auto.StopAsync(disconnect: true).ConfigureAwait(true);
            }

            await _connection.DisconnectAsync().ConfigureAwait(true);

            if (_systemProxy is not null)
            {
                var proxy = await Task.Run(() => _systemProxy.Reset(_settings.Current.HttpPort)).ConfigureAwait(true);
                if (proxy != SystemProxyReset.NothingToDo)
                {
                    lines.Add(Localizer.Get($"ResetProxy_{proxy}"));
                }
            }

            if (await _networkReset.ResetAsync().ConfigureAwait(true) is { } service)
            {
                lines.AddRange(service.Items.Select(DescribeResetItem).OfType<string>());
            }
            else
            {
                // Службы нет или она не отвечает — то же самое с правами администратора (запрос UAC).
                var elevated = await _service.RunElevatedSetupAsync(ServiceSetupAction.ResetNetwork, CancellationToken.None).ConfigureAwait(true);
                lines.Add(elevated is ServiceSetupResult.Cancelled or ServiceSetupResult.SetupNotFound
                    ? Localizer.Get($"ServiceSetup_{elevated}")
                    : Localizer.Get($"ServiceSetup_ResetNetwork_{elevated}"));
            }
        }
        finally
        {
            IsResetting = false;
        }

        ResetResult = lines.Count == 0
            ? Localizer.Get("ResetNothingToDo")
            : Localizer.Get("ResetDone") + Environment.NewLine + string.Join(Environment.NewLine, lines.Select(l => "• " + l));
        await RefreshKillSwitchAsync().ConfigureAwait(true);
        await RefreshServiceStatusAsync().ConfigureAwait(true);
    }

    private static string? DescribeResetItem(IpcResetItem item) => item.Outcome switch
    {
        IpcResetOutcome.Done => Localizer.Get($"ResetStep_{item.Step}"),
        IpcResetOutcome.Failed => Localizer.Format("ResetStepFailedFormat", Localizer.Get($"ResetStepName_{item.Step}"), item.Error ?? "?"),
        _ => null,
    };
}

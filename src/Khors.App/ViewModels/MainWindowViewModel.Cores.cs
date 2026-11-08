using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Engines;
using Khors.Engines.Connection;
using Khors.Engines.Cores;

namespace Khors.App.ViewModels;

/// <summary>Строка ядра в «Настройки → Ядра».</summary>
/// <param name="Available">Строка о доступном обновлении или новой серии; <c>null</c> — нечего сказать.</param>
public sealed record CoreRowItem(CoreKind Kind, string Name, string LocalText, string ServiceText, string? Available, bool CanRollback, string? RollbackText);

/// <summary>
/// Ядра (ROADMAP 5.2): версии окна и службы, «Проверить обновления», «Обновить», «Откатить». Сеть — только по кнопке.
/// </summary>
public sealed partial class MainWindowViewModel
{
    public ObservableCollection<CoreRowItem> CoreRows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCoresCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCoresCommand))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCoreCommand))]
    public partial bool IsCoresBusy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateCoresCommand))]
    public partial bool HasCoreUpdates { get; set; }

    /// <summary>Итог последнего действия с ядрами.</summary>
    [ObservableProperty]
    public partial string? CoresMessage { get; set; }

    public bool HasCoresSupport => _cores is not null;

    private void InitCores()
    {
        ShowCores();
        if (_cores is not null)
        {
            _ = RefreshCoresServiceAsync();
        }
    }

    private async Task RefreshCoresServiceAsync()
    {
        await Task.Run(() => _cores!.RefreshServiceAsync()).ConfigureAwait(true);
        ShowCores();
    }

    private bool CanUseCores() => _cores is not null && !IsCoresBusy;

    private bool CanUpdateCores() => CanUseCores() && HasCoreUpdates;

    [RelayCommand(CanExecute = nameof(CanUseCores))]
    private async Task CheckCoresAsync()
    {
        IsCoresBusy = true;
        CoresMessage = Localizer.Get("CoresChecking");
        try
        {
            await Task.Run(() => _cores!.CheckAsync()).ConfigureAwait(true);
        }
        finally
        {
            IsCoresBusy = false;
        }

        ShowCores();
        var states = _cores!.Current;
        CoresMessage = states.Any(s => s.CheckError is not null)
            ? string.Join(" ", states.Where(s => s.CheckError is not null).Select(s => Localizer.Format("CoresCheckFailedFormat", CoreName(s.Kind), Localizer.Get($"CoreUpdateError_{s.CheckError}"))))
            : states.Any(s => s.Available is not null) ? null : Localizer.Get("CoresUpToDate");
    }

    [RelayCommand(CanExecute = nameof(CanUpdateCores))]
    private async Task UpdateCoresAsync()
    {
        IsCoresBusy = true;
        CoresMessage = Localizer.Get("CoresUpdating");
        IReadOnlyList<CoreActionOutcome> outcomes;
        try
        {
            outcomes = await Task.Run(() => _cores!.UpdateAsync()).ConfigureAwait(true);
        }
        finally
        {
            IsCoresBusy = false;
        }

        ShowCores();
        CoresMessage = string.Join(Environment.NewLine, outcomes.Select(DescribeCoreOutcome)) + ReconnectNote();
    }

    [RelayCommand(CanExecute = nameof(CanUseCores))]
    private async Task RollbackCoreAsync(CoreRowItem? row)
    {
        if (row is null)
        {
            return;
        }

        IsCoresBusy = true;
        CoreActionOutcome outcome;
        try
        {
            outcome = await Task.Run(() => _cores!.RollbackAsync(row.Kind)).ConfigureAwait(true);
        }
        finally
        {
            IsCoresBusy = false;
        }

        ShowCores();
        CoresMessage = DescribeCoreOutcome(outcome) + ReconnectNote();
    }

    private string ReconnectNote() =>
        State == ConnectionState.Connected ? Environment.NewLine + Localizer.Get("CoresReconnectHint") : string.Empty;

    private static string CoreName(CoreKind kind) => Localizer.CoreName(kind);

    private static string DescribeCoreOutcome(CoreActionOutcome outcome)
    {
        var name = CoreName(outcome.Kind);
        var parts = new List<string>();
        if (outcome.LocalError is { } localError)
        {
            parts.Add(Localizer.Format("CoresLocalFailedFormat", Localizer.Get($"CoreUpdateError_{localError}"))
                + (outcome.LocalDetail is { } detail ? " (" + detail + ")" : string.Empty));
        }

        if (outcome.ServiceError is { } serviceError)
        {
            var reason = serviceError == "Unavailable" ? Localizer.Get("CoresServiceUnavailable") : Localizer.Get($"CoreUpdateError_{serviceError}");
            parts.Add(Localizer.Format("CoresServiceFailedFormat", reason) + (outcome.ServiceDetail is { } detail ? " (" + detail + ")" : string.Empty));
        }

        return parts.Count == 0
            ? outcome.Version is { } version ? Localizer.Format("CoresUpdatedFormat", name, version) : Localizer.Format("CoresRolledBackFormat", name)
            : name + ": " + string.Join(" ", parts);
    }

    private void ShowCores()
    {
        CoreRows.Clear();
        if (_cores is null)
        {
            HasCoreUpdates = false;
            return;
        }

        var states = _cores.Current;
        foreach (var state in states)
        {
            var local = Localizer.Format(state.Local.Shipped ? "CoreLocalShippedFormat" : "CoreLocalFormat", state.Local.Version);
            var service = state.Service is { } s
                ? Localizer.Format(s.Shipped ? "CoreServiceShippedFormat" : "CoreServiceFormat", s.Version)
                : Localizer.Get("CoreServiceUnknown");
            var available = state.Available is { } release
                ? Localizer.Format("CoreAvailableFormat", release.Version)
                : state.NewerSeries is { } newer ? Localizer.Format("CoreNewerSeriesFormat", newer) : null;
            var previous = state.Local.PreviousVersion ?? state.Service?.PreviousVersion;
            CoreRows.Add(new CoreRowItem(
                state.Kind,
                CoreName(state.Kind),
                local,
                service,
                available,
                previous is not null,
                previous is null ? null : Localizer.Format("CoreRollbackFormat", previous)));
        }

        HasCoreUpdates = states.Any(s => s.Available is not null);
    }
}

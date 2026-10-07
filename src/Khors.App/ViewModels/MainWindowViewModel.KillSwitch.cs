using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Engines.Connection;
using Khors.Ipc;

namespace Khors.App.ViewModels;

/// <summary>
/// Kill Switch (ROADMAP 4.1): настройка живёт в службе — окно её показывает и меняет по IPC. Действует только в режиме
/// TUN; «Постоянно» блокирует трафик и без подключения, поэтому вместе с ним режим «Системный прокси» недоступен.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly IServiceKillSwitchClient _killSwitch;

    /// <summary>Показ ответа службы — без отправки настройки обратно.</summary>
    private bool _applyingKillSwitch;

    [ObservableProperty]
    public partial bool IsKillSwitchOff { get; set; } = true;

    [ObservableProperty]
    public partial bool IsKillSwitchWhileConnected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectProxy))]
    public partial bool IsKillSwitchAlways { get; set; }

    [ObservableProperty]
    public partial bool KillSwitchAllowLan { get; set; } = true;

    /// <summary>Трафик заблокирован (фильтры стоят, туннеля нет): текст для страницы «Подключение».</summary>
    [ObservableProperty]
    public partial string? KillSwitchBanner { get; set; }

    [ObservableProperty]
    public partial bool CanReleaseKillSwitch { get; set; }

    /// <summary>Ошибка службы при установке фильтров; <c>null</c> — нет.</summary>
    [ObservableProperty]
    public partial string? KillSwitchError { get; set; }

    public bool CanSelectProxy => CanChangeMode && !IsKillSwitchAlways;

    /// <summary>«Постоянно» — только в режиме TUN (иначе ядра окна оказались бы за блокировкой).</summary>
    public bool CanSelectKillSwitchAlways => IsServiceRunning && (IsTunMode || IsKillSwitchAlways);

    partial void OnIsKillSwitchOffChanged(bool value) => SetKillSwitchMode(value, IpcKillSwitchMode.Off);

    partial void OnIsKillSwitchWhileConnectedChanged(bool value) => SetKillSwitchMode(value, IpcKillSwitchMode.WhileConnected);

    partial void OnIsKillSwitchAlwaysChanged(bool value) => SetKillSwitchMode(value, IpcKillSwitchMode.Always);

    partial void OnKillSwitchAllowLanChanged(bool value)
    {
        if (!_applyingKillSwitch)
        {
            _ = SendKillSwitchAsync(CurrentKillSwitchMode(), value);
        }
    }

    private void SetKillSwitchMode(bool selected, IpcKillSwitchMode mode)
    {
        if (selected && !_applyingKillSwitch)
        {
            _ = SendKillSwitchAsync(mode, KillSwitchAllowLan);
        }
    }

    private IpcKillSwitchMode CurrentKillSwitchMode() =>
        IsKillSwitchAlways ? IpcKillSwitchMode.Always : IsKillSwitchWhileConnected ? IpcKillSwitchMode.WhileConnected : IpcKillSwitchMode.Off;

    private async Task SendKillSwitchAsync(IpcKillSwitchMode mode, bool allowLan)
    {
        var status = await _killSwitch.SetAsync(mode, allowLan).ConfigureAwait(true);
        if (status is null)
        {
            Message = Localizer.Get("KillSwitchNoAnswer");
            await RefreshKillSwitchAsync().ConfigureAwait(true);
            return;
        }

        ShowKillSwitch(status);
    }

    [RelayCommand]
    private async Task ReleaseKillSwitchAsync()
    {
        if (await _killSwitch.ReleaseAsync().ConfigureAwait(true) is { } status)
        {
            ShowKillSwitch(status);
        }
    }

    /// <summary>Состояние из службы: после её проверки и после каждого подключения и отключения.</summary>
    private async Task RefreshKillSwitchAsync()
    {
        if (!IsServiceRunning)
        {
            ShowKillSwitch(null);
            return;
        }

        ShowKillSwitch(await _killSwitch.GetStatusAsync().ConfigureAwait(true));
    }

    private void ShowKillSwitch(KillSwitchStatusResponse? status)
    {
        _applyingKillSwitch = true;
        try
        {
            var mode = status?.Mode ?? IpcKillSwitchMode.Off;
            IsKillSwitchOff = mode == IpcKillSwitchMode.Off;
            IsKillSwitchWhileConnected = mode == IpcKillSwitchMode.WhileConnected;
            IsKillSwitchAlways = mode == IpcKillSwitchMode.Always;
            KillSwitchAllowLan = status?.AllowLocalNetwork ?? true;
        }
        finally
        {
            _applyingKillSwitch = false;
        }

        // Блокировку показываем, только когда подключения нет: во время запуска TUN фильтры стоят, а адаптера ещё нет.
        var blocking = status is { Blocking: true } && State is not (Engines.Connection.ConnectionState.Connected or Engines.Connection.ConnectionState.Connecting);
        KillSwitchBanner = blocking
            ? Localizer.Get(status!.Mode == IpcKillSwitchMode.Always ? "KillSwitchBlockingAlways" : "KillSwitchBlockingWhileConnected")
            : null;
        CanReleaseKillSwitch = blocking && status!.Mode == IpcKillSwitchMode.WhileConnected;
        KillSwitchError = status?.Error is { } error ? Localizer.Format("KillSwitchErrorFormat", error) : null;
        OnPropertyChanged(nameof(CanSelectKillSwitchAlways));
        OnPropertyChanged(nameof(CanSelectProxy));
    }
}

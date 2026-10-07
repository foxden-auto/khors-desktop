using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Engines.Connection;

namespace Khors.App.ViewModels;

/// <summary>Переподключение после обрыва, смены сети и сна (ROADMAP 4.3): настройка и ход попыток.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Переподключаться автоматически, если подключение оборвалось само.</summary>
    [ObservableProperty]
    public partial bool AutoReconnect { get; set; }

    public bool IsReconnecting => State == ConnectionState.Reconnecting;

    private void LoadReconnectSetting() => AutoReconnect = _settings.Current.AutoReconnect;

    partial void OnAutoReconnectChanged(bool value)
    {
        if (value != _settings.Current.AutoReconnect)
        {
            _settings.Update(s => s with { AutoReconnect = value });
        }
    }

    /// <summary>Строка под статусом: ждём сеть, попытка идёт или будет через N с (обновляется раз в секунду).</summary>
    private static string? DescribeReconnect(ConnectionStatus status) => status.Reconnect switch
    {
        null => null,
        { WaitingForNetwork: true } => Localizer.Get("ReconnectWaitingForNetwork"),
        { RetryAt: { } at } progress => Localizer.Format(
            "ReconnectRetryFormat",
            progress.Attempt,
            Math.Max(1, (int)Math.Ceiling((at - DateTimeOffset.UtcNow).TotalSeconds))),
        var progress => Localizer.Format("ReconnectAttemptFormat", progress.Attempt),
    };

    private void UpdateReconnectCountdown()
    {
        if (_connection.Status is { State: ConnectionState.Reconnecting, Reconnect.RetryAt: not null } status)
        {
            StatusDetail = DescribeReconnect(status);
        }
    }
}

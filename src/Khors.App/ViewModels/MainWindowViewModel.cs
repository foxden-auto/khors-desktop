using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Core.Storage;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Khors.Engines.Storage;
using Khors.Engines.Subscriptions;

namespace Khors.App.ViewModels;

/// <summary>Главное окно: статус и кнопка подключения, список профилей, импорт из буфера.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly ProfileRepository _profiles;
    private readonly SettingsStore _settings;
    private readonly ConnectionManager _connection;
    private readonly IClipboardText _clipboard;
    private readonly ICoreLauncher _launcher;
    private readonly SubscriptionUpdater _subscriptionUpdater;
    private readonly DispatcherTimer _sessionTimer;
    private readonly Dictionary<Guid, LatencyResult> _latency = [];

    public MainWindowViewModel(
        ProfileRepository profiles,
        SettingsStore settings,
        ConnectionManager connection,
        IClipboardText clipboard,
        ICoreLauncher launcher,
        SubscriptionUpdater subscriptionUpdater)
    {
        _subscriptionUpdater = subscriptionUpdater;
        _profiles = profiles;
        _settings = settings;
        _connection = connection;
        _clipboard = clipboard;
        _launcher = launcher;
        _sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateSessionTime());

        ReloadProfiles();
        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.Current.SelectedProfileId) ?? Profiles.FirstOrDefault();
        ApplyStatus(connection.Status);

        _profiles.Changed += OnProfilesChanged;
        _connection.StatusChanged += OnConnectionStatusChanged;
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public ObservableCollection<SubscriptionItemViewModel> Subscriptions { get; } = [];

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public string SubscriptionsHeader => Localizer.Format("SubscriptionsHeaderFormat", Subscriptions.Count);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    public partial string? ActiveProfileName { get; set; }

    [ObservableProperty]
    public partial string? SessionTime { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    public partial ConnectionState State { get; set; }

    [ObservableProperty]
    public partial string ConnectButtonText { get; set; } = Localizer.Get("ButtonConnect");

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? LogTail { get; set; }

    /// <summary>Задержка текущего подключения («Задержка: 123 мс»).</summary>
    [ObservableProperty]
    public partial string? ConnectionLatency { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLatencyCommand))]
    public partial bool IsTestingLatency { get; set; }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsFailed => State == ConnectionState.Failed;

    public bool HasLogTail => !string.IsNullOrEmpty(LogTail);

    public bool HasNoProfiles => Profiles.Count == 0;

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string ModeText { get; } = Localizer.Get("ModeSystemProxy");

    /// <summary>Сообщения после запуска: восстановление прокси, состояние файла профилей.</summary>
    public void ShowStartupNotices(bool proxyRecovered)
    {
        var notices = new List<string>();
        if (proxyRecovered)
        {
            notices.Add(Localizer.Get("ProxyRecoveredAfterCrash"));
        }

        var load = _profiles.LoadResult;
        if (_profiles.IsReadOnly)
        {
            notices.Add(Localizer.Get("StorageReadOnly"));
        }
        else if (load.FromBackup)
        {
            notices.Add(Localizer.Get("StorageRestoredFromBackup"));
        }
        else if (load.Status == StorageLoadStatus.Corrupt)
        {
            notices.Add(Localizer.Get("StorageCorrupt"));
        }

        if (notices.Count > 0)
        {
            Message = string.Join(Environment.NewLine, notices);
        }
    }

    public void Dispose()
    {
        _sessionTimer.Stop();
        _profiles.Changed -= OnProfilesChanged;
        _connection.StatusChanged -= OnConnectionStatusChanged;
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            await _connection.DisconnectAsync().ConfigureAwait(true);
            return;
        }

        if (SelectedProfile is null)
        {
            return;
        }

        var settings = _settings.Current;
        Message = null;
        await _connection.ConnectAsync(
            SelectedProfile.Profile,
            new CoreStartPreferences(settings.SocksPort, settings.HttpPort, settings.CoreLogLevel)).ConfigureAwait(true);
    }

    private bool CanToggleConnection() =>
        State is ConnectionState.Connected or ConnectionState.Connecting
        || (State is ConnectionState.Disconnected or ConnectionState.Failed && SelectedProfile is not null);

    [RelayCommand]
    private async Task ImportFromClipboardAsync()
    {
        var text = await _clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            Message = Localizer.Get("ClipboardEmpty");
            return;
        }

        if (_profiles.IsReadOnly)
        {
            Message = Localizer.Get("StorageReadOnly");
            return;
        }

        // Адрес подписки: добавить и сразу загрузить.
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var subscriptionUrl) && subscriptionUrl.Scheme is "http" or "https")
        {
            var subscription = _profiles.AddSubscription(subscriptionUrl);
            await UpdateSubscriptionCoreAsync(subscription.Id, added: true).ConfigureAwait(true);
            return;
        }

        var result = _profiles.Import(text);
        var lines = new List<string> { Localizer.Format("ImportResultFormat", result.Added.Count, result.Duplicates, result.Errors.Count) };
        lines.AddRange(result.Errors.Take(5).Select(e => Localizer.Format("ImportLineErrorFormat", e.Line, Localizer.Describe(e.Error))));
        Message = string.Join(Environment.NewLine, lines);

        if (result.Added.Count > 0)
        {
            var firstAdded = result.Added[0].Id;
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == firstAdded) ?? SelectedProfile;
        }
    }

    /// <summary>Тест задержки всех профилей: подключённый — через текущее ядро, остальные — через временное (не больше 3 одновременно).</summary>
    [RelayCommand(CanExecute = nameof(CanTestLatency))]
    private async Task TestLatencyAsync()
    {
        IsTestingLatency = true;
        try
        {
            var items = Profiles.ToList();
            foreach (var item in items)
            {
                item.SetLatency(null);
            }

            using var parallel = new SemaphoreSlim(3);
            await Task.WhenAll(items.Select(async item =>
            {
                await parallel.WaitAsync().ConfigureAwait(true);
                try
                {
                    var result = await MeasureAsync(item).ConfigureAwait(true);
                    _latency[item.Id] = result;
                    item.SetLatency(result);
                }
                finally
                {
                    parallel.Release();
                }
            })).ConfigureAwait(true);
        }
        finally
        {
            IsTestingLatency = false;
        }
    }

    private bool CanTestLatency() => !IsTestingLatency;

    private Task<LatencyResult> MeasureAsync(ProfileItemViewModel item)
    {
        var url = LatencyUrl();
        return _connection.Status is { State: ConnectionState.Connected, HttpPort: { } port, Profile: { } active } && active.Id == item.Id
            ? LatencyTester.MeasureThroughProxyAsync(port, url, LatencyTester.DefaultTimeout)
            : LatencyTester.MeasureProfileAsync(item.Profile, _launcher, url, LatencyTester.DefaultTimeout);
    }

    private Uri LatencyUrl() =>
        Uri.TryCreate(_settings.Current.LatencyTestUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? url
            : LatencyTester.DefaultTestUrl;

    /// <summary>После подключения — один замер через текущее ядро.</summary>
    private async Task MeasureConnectionAsync(ConnectionStatus status)
    {
        if (status is not { HttpPort: { } port, Profile: { } profile })
        {
            return;
        }

        ConnectionLatency = Localizer.Format("ConnectionLatencyFormat", Localizer.Get("LatencyTesting"));
        var result = await LatencyTester.MeasureThroughProxyAsync(port, LatencyUrl(), LatencyTester.DefaultTimeout).ConfigureAwait(true);

        // Пока шёл замер, могли отключиться или переключиться.
        if (_connection.Status is { State: ConnectionState.Connected, Profile: { } current } && current.Id == profile.Id)
        {
            ConnectionLatency = Localizer.Format("ConnectionLatencyFormat", Localizer.Describe(result));
            _latency[profile.Id] = result;
            Profiles.FirstOrDefault(p => p.Id == profile.Id)?.SetLatency(result);
        }
    }

    [RelayCommand]
    private Task UpdateSubscriptionAsync(SubscriptionItemViewModel? item) =>
        item is null ? Task.CompletedTask : UpdateSubscriptionCoreAsync(item.Id, added: false);

    [RelayCommand]
    private async Task RemoveSubscriptionAsync(SubscriptionItemViewModel? item)
    {
        if (item is null || _profiles.IsReadOnly)
        {
            return;
        }

        if (_connection.Status.Profile?.SubscriptionId == item.Id && State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            await _connection.DisconnectAsync().ConfigureAwait(true);
        }

        _profiles.RemoveSubscription(item.Id);
    }

    private async Task UpdateSubscriptionCoreAsync(Guid id, bool added)
    {
        var item = Subscriptions.FirstOrDefault(s => s.Id == id);
        if (item is not null)
        {
            item.IsUpdating = true;
        }

        var outcome = await _subscriptionUpdater.UpdateAsync(id).ConfigureAwait(true);
        var name = _profiles.FindSubscription(id)?.Name ?? string.Empty;
        var lines = new List<string>
        {
            outcome switch
            {
                { Error: { } error } => Localizer.Format("SubscriptionUpdateFailedFormat", name, Localizer.Describe(error)),
                _ when added => Localizer.Format("SubscriptionAddedFormat", name, outcome.Total),
                _ => Localizer.Format("SubscriptionUpdatedFormat", name, outcome.Total, outcome.Added, outcome.Removed),
            },
        };
        if (outcome.ViaProxy && outcome.Error is null)
        {
            lines.Add(Localizer.Get("SubscriptionViaProxy"));
        }

        Message = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileItemViewModel? item)
    {
        if (item is null || _profiles.IsReadOnly)
        {
            return;
        }

        if (_connection.Status.Profile?.Id == item.Id && State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            await _connection.DisconnectAsync().ConfigureAwait(true);
        }

        _profiles.Remove(item.Id);
    }

    partial void OnSelectedProfileChanged(ProfileItemViewModel? value)
    {
        if (value is not null && value.Id != _settings.Current.SelectedProfileId)
        {
            _settings.Update(s => s with { SelectedProfileId = value.Id });
        }
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsFailed));
    }

    partial void OnLogTailChanged(string? value) => OnPropertyChanged(nameof(HasLogTail));

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    private void OnProfilesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ReloadProfiles);

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));

    private void ReloadProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in _profiles.Profiles)
        {
            var item = new ProfileItemViewModel(profile);
            if (_latency.TryGetValue(profile.Id, out var latency))
            {
                item.SetLatency(latency);
            }

            Profiles.Add(item);
        }

        SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        MarkActiveProfile();
        OnPropertyChanged(nameof(HasNoProfiles));

        Subscriptions.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in _profiles.Subscriptions)
        {
            Subscriptions.Add(new SubscriptionItemViewModel(subscription, now));
        }

        OnPropertyChanged(nameof(HasSubscriptions));
        OnPropertyChanged(nameof(SubscriptionsHeader));
    }

    private void ApplyStatus(ConnectionStatus status)
    {
        var justConnected = status.State == ConnectionState.Connected && State != ConnectionState.Connected;
        State = status.State;
        ActiveProfileName = status.Profile?.Name;
        StatusText = Localizer.Get($"Status{status.State}");
        ConnectButtonText = Localizer.Get(status.State is ConnectionState.Connected or ConnectionState.Connecting ? "ButtonDisconnect" : "ButtonConnect");

        StatusDetail = status switch
        {
            { State: ConnectionState.Connected, HttpPort: { } http, SocksPort: { } socks } => Localizer.Format("LocalProxyFormat", http, socks),
            { State: ConnectionState.Failed, Failure: { } failure } => Localizer.Describe(failure),
            { State: ConnectionState.Disconnected } when SelectedProfile is null => Localizer.Get("NoProfileSelected"),
            _ => null,
        };

        LogTail = status.Failure is { LogTail.Count: > 0 } withLog ? string.Join(Environment.NewLine, withLog.LogTail) : null;

        if (status.State == ConnectionState.Connected)
        {
            _sessionTimer.Start();
        }
        else
        {
            _sessionTimer.Stop();
            SessionTime = null;
        }

        UpdateSessionTime();
        MarkActiveProfile();

        if (status.State != ConnectionState.Connected)
        {
            ConnectionLatency = null;
        }
        else if (justConnected)
        {
            _ = MeasureConnectionAsync(status);
        }
    }

    private void UpdateSessionTime()
    {
        if (_connection.Status is { State: ConnectionState.Connected, ConnectedAt: { } since })
        {
            var elapsed = DateTimeOffset.UtcNow - since;
            SessionTime = Localizer.Format("SessionTimeFormat", elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
        }
    }

    private void MarkActiveProfile()
    {
        var activeId = _connection.Status is { State: ConnectionState.Connected or ConnectionState.Connecting, Profile: { } profile } ? profile.Id : (Guid?)null;
        foreach (var item in Profiles)
        {
            item.IsActive = item.Id == activeId;
        }
    }
}

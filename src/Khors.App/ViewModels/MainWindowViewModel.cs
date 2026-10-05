using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Khors.Core.Import;
using Khors.Core.Qr;
using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Khors.Engines.Storage;
using Khors.Engines.Subscriptions;

namespace Khors.App.ViewModels;

/// <summary>Главное окно: статус и кнопка подключения, список профилей, импорт из буфера.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IProfileActions, IDisposable
{
    private readonly ProfileRepository _profiles;
    private readonly SettingsStore _settings;
    private readonly ConnectionManager _connection;
    private readonly IAppClipboard _clipboard;
    private readonly IDesktopDialogs _dialogs;
    private readonly ICoreLauncher _launcher;
    private readonly SubscriptionUpdater _subscriptionUpdater;
    private readonly DispatcherTimer _sessionTimer;
    private readonly Dictionary<Guid, LatencyResult> _latency = [];
    private Khors.Engines.Processes.CoreLogBuffer? _liveLog;
    private int _liveLogRefreshQueued;

    public MainWindowViewModel(
        ProfileRepository profiles,
        SettingsStore settings,
        ConnectionManager connection,
        IAppClipboard clipboard,
        IDesktopDialogs dialogs,
        ICoreLauncher launcher,
        SubscriptionUpdater subscriptionUpdater)
    {
        _subscriptionUpdater = subscriptionUpdater;
        _profiles = profiles;
        _settings = settings;
        _connection = connection;
        _clipboard = clipboard;
        _dialogs = dialogs;
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
        WatchLiveLog(null);
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

    /// <summary>Текст из буфера; если текста нет — QR-коды с картинки в буфере.</summary>
    [RelayCommand]
    private async Task ImportFromClipboardAsync()
    {
        var text = await _clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            using var bitmap = await _clipboard.GetBitmapAsync().ConfigureAwait(true);
            if (bitmap is null)
            {
                Message = Localizer.Get("ClipboardEmpty");
                return;
            }

            text = await DecodeQrAsync(bitmap).ConfigureAwait(true);
            if (text is null)
            {
                Message = Localizer.Get("ClipboardNoQr");
                return;
            }
        }

        await ImportTextAsync(text).ConfigureAwait(true);
    }

    /// <summary>Файл: картинка — QR-коды с неё, иначе текст (ссылки, base64, конфиг Clash/sing-box/Xray).</summary>
    [RelayCommand]
    private async Task ImportFromFileAsync()
    {
        PickedFile? file;
        try
        {
            file = await _dialogs.PickImportFileAsync().ConfigureAwait(true);
        }
        catch (FileTooLargeException)
        {
            Message = Localizer.Get("FileTooLarge");
            return;
        }
        catch (IOException)
        {
            Message = Localizer.Get("FileReadFailed");
            return;
        }

        if (file is null)
        {
            return;
        }

        string? text;
        if (file.IsImage)
        {
            try
            {
                using var bitmap = new Bitmap(new MemoryStream(file.Content));
                text = await DecodeQrAsync(bitmap).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                text = null;
            }

            if (text is null)
            {
                Message = Localizer.Get("FileNoQr");
                return;
            }
        }
        else
        {
            text = System.Text.Encoding.UTF8.GetString(file.Content);
            if (string.IsNullOrWhiteSpace(text))
            {
                Message = Localizer.Get("FileEmpty");
                return;
            }
        }

        await ImportTextAsync(text).ConfigureAwait(true);
    }

    /// <summary>Тексты всех QR-кодов картинки построчно; <c>null</c> — кодов нет. Распознавание — не в потоке окна.</summary>
    private static async Task<string?> DecodeQrAsync(Bitmap bitmap)
    {
        var (pixels, width, height) = BitmapPixels.ToBgra(bitmap);
        var codes = await Task.Run(() => QrCodes.Decode(pixels, width, height)).ConfigureAwait(true);
        return codes.Count > 0 ? string.Join('\n', codes) : null;
    }

    private async Task ImportTextAsync(string text)
    {
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

    /// <summary>Весь лог ядра (до 1000 строк, уже замаскирован) — в буфер обмена, чтобы приложить к сообщению об ошибке.</summary>
    [RelayCommand]
    private async Task CopyCoreLogAsync()
    {
        var lines = _connection.Log?.Snapshot().Select(l => l.Text).ToList() is { Count: > 0 } live ? live : LogTail?.Split(Environment.NewLine).ToList();
        if (lines is { Count: > 0 })
        {
            await _clipboard.SetTextAsync(string.Join(Environment.NewLine, lines)).ConfigureAwait(true);
            Message = Localizer.Format("CoreLogCopiedFormat", lines.Count);
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

    /// <summary>
    /// Ручной выбор ядра (docs/SPEC.md, 3.4). Ядро, которое не запустит профиль, не сохраняется — вместо этого
    /// объяснение. Если профиль подключён, он переподключается на новом ядре.
    /// </summary>
    public async Task SetCoreAsync(ProfileItemViewModel item, CorePreference core)
    {
        if (_profiles.IsReadOnly || item.Profile.Core == core)
        {
            ReloadProfiles();
            return;
        }

        var updated = item.Profile with { Core = core };
        if (core != CorePreference.Auto && CoreSelection.Select(updated) is { Unsupported: { } field } choice)
        {
            Message = Localizer.Format("CoreChoiceRejectedFormat", Localizer.CoreName(choice.Core), Localizer.DescribeUnsupported(field));
            // Пункт меню уже отмечен — вернуть отметку на сохранённый выбор.
            ReloadProfiles();
            return;
        }

        Message = null;
        var reconnect = _connection.Status.Profile?.Id == item.Id && State is ConnectionState.Connected or ConnectionState.Connecting;
        _profiles.Update(updated);
        if (reconnect)
        {
            var settings = _settings.Current;
            await _connection.ConnectAsync(updated, new CoreStartPreferences(settings.SocksPort, settings.HttpPort, settings.CoreLogLevel)).ConfigureAwait(true);
        }
    }

    /// <summary>Ссылка профиля — в буфер обмена. В ней ключи доступа: предупреждаем, в лог не пишем.</summary>
    public async Task CopyLinkAsync(ProfileItemViewModel item)
    {
        await _clipboard.SetTextAsync(ShareLinkExporter.Export(item.Profile)).ConfigureAwait(true);
        Message = Localizer.Format("LinkCopiedFormat", item.Name);
    }

    /// <summary>QR-код ссылки профиля в отдельном окне.</summary>
    public async Task ShowQrAsync(ProfileItemViewModel item)
    {
        if (QrCodes.Encode(ShareLinkExporter.Export(item.Profile)) is not { } matrix)
        {
            Message = Localizer.Get("QrTooLong");
            return;
        }

        // Около 1000 пикселей: чёткий код и в окне, и в скопированной картинке. Освобождает окно QR при закрытии.
        var bitmap = BitmapPixels.Render(matrix, scale: Math.Max(4, 1000 / (matrix.Size + 8)), ThemeColor("KhorsQrDarkColor"), ThemeColor("KhorsQrLightColor"));
        await _dialogs.ShowQrAsync(item.Name, bitmap).ConfigureAwait(true);
    }

    private static Color ThemeColor(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, null, out var value) == true && value is Color color ? color : default;

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
            var item = new ProfileItemViewModel(profile, this);
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
            { State: ConnectionState.Connected, HttpPort: { } http, SocksPort: { } socks } => Localizer.Format("LocalProxyFormat", http, socks, Localizer.CoreName(status.Core)),
            { State: ConnectionState.Failed, Failure: { } failure } => Localizer.Describe(failure),
            { State: ConnectionState.Disconnected } when SelectedProfile is null => Localizer.Get("NoProfileSelected"),
            _ => null,
        };

        // Лог ядра: при ошибке — хвост из причины, при подключении — живой (уже замаскирован при поступлении).
        WatchLiveLog(status.State == ConnectionState.Connected ? _connection.Log : null);
        if (status.Failure is { LogTail.Count: > 0 } withLog)
        {
            LogTail = string.Join(Environment.NewLine, withLog.LogTail);
        }
        else if (status.State != ConnectionState.Connected)
        {
            LogTail = null;
        }

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

    private void WatchLiveLog(Khors.Engines.Processes.CoreLogBuffer? log)
    {
        if (ReferenceEquals(log, _liveLog))
        {
            return;
        }

        if (_liveLog is not null)
        {
            _liveLog.LineAdded -= OnLiveLogLine;
        }

        _liveLog = log;
        if (log is not null)
        {
            log.LineAdded += OnLiveLogLine;
            RefreshLiveLog();
        }
    }

    // Строки приходят в потоке чтения лога; обновляем окно не чаще двух раз в секунду.
    private void OnLiveLogLine(object? sender, Khors.Engines.Processes.CoreLogLine line)
    {
        if (Interlocked.Exchange(ref _liveLogRefreshQueued, 1) == 0)
        {
            DispatcherTimer.RunOnce(RefreshLiveLog, TimeSpan.FromMilliseconds(500));
        }
    }

    private void RefreshLiveLog()
    {
        Interlocked.Exchange(ref _liveLogRefreshQueued, 0);
        if (_liveLog is { } log)
        {
            var tail = log.Tail(30);
            LogTail = tail.Count > 0 ? string.Join(Environment.NewLine, tail) : null;
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

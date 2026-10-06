using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Qr;
using Khors.Core.Storage;
using Khors.Engines.Auto;
using Khors.Engines.Connection;
using Khors.Engines.Latency;
using Khors.Engines.Storage;
using Khors.Engines.Subscriptions;
using Khors.Ipc;
using Khors.Platform;

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
    private readonly AutoConnector _auto;
    private readonly IServiceControl _service;
    private readonly IIpcClientTransport _serviceTransport;
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
        SubscriptionUpdater subscriptionUpdater,
        AutoConnector auto,
        IServiceControl service,
        IIpcClientTransport serviceTransport)
    {
        _auto = auto;
        _service = service;
        _serviceTransport = serviceTransport;
        _subscriptionUpdater = subscriptionUpdater;
        _profiles = profiles;
        _settings = settings;
        _connection = connection;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _launcher = launcher;
        _sessionTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnSessionTick());

        IsTunMode = settings.Current.ConnectionMode == ConnectionMode.Tun;

        // «Авто» — до загрузки списка, чтобы загрузка не выбрала первый профиль поверх сохранённого выбора.
        IsAutoSelected = settings.Current.AutoSelect;
        SortByLatency = settings.Current.SortProfilesByLatency;
        ReloadProfiles();
        if (!IsAutoSelected)
        {
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.Current.SelectedProfileId) ?? Profiles.FirstOrDefault();
        }

        UpdateAutoSummary();
        ApplyStatus(connection.Status);

        _profiles.Changed += OnProfilesChanged;
        _connection.StatusChanged += OnConnectionStatusChanged;
        _auto.StatusChanged += OnAutoStatusChanged;
        _auto.Measured += OnAutoMeasured;
        _ = RefreshServiceStatusAsync();
    }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; } = [];

    public ObservableCollection<SubscriptionItemViewModel> Subscriptions { get; } = [];

    public bool HasSubscriptions => Subscriptions.Count > 0;

    public string SubscriptionsHeader => Localizer.Format("SubscriptionsHeaderFormat", Subscriptions.Count);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(ListSelectedProfile))]
    public partial ProfileItemViewModel? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    /// <summary>Подсказка по причине из лога ядра: при ошибке подключения или когда соединения с сервером не проходят.</summary>
    [ObservableProperty]
    public partial string? ProblemHint { get; set; }

    [ObservableProperty]
    public partial string? ActiveProfileName { get; set; }

    /// <summary>Время сессии «01:02:03»; <c>null</c> — не подключено.</summary>
    [ObservableProperty]
    public partial string? SessionTime { get; set; }

    /// <summary>Имя подключённого профиля без флага.</summary>
    [ObservableProperty]
    public partial string? SessionServer { get; set; }

    /// <summary>Протокол подключённого профиля: «VLESS · REALITY · TCP».</summary>
    [ObservableProperty]
    public partial string? SessionProtocol { get; set; }

    /// <summary>Режим и ядро подключения: «Прокси · Xray».</summary>
    [ObservableProperty]
    public partial string? SessionModeCore { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    public partial ConnectionState State { get; set; }

    [ObservableProperty]
    public partial string ConnectButtonText { get; set; } = Localizer.Get("ButtonConnect");

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? LogTail { get; set; }

    /// <summary>Задержка текущего подключения («123 мс», «проверка…»).</summary>
    [ObservableProperty]
    public partial string? ConnectionLatency { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestLatencyCommand))]
    public partial bool IsTestingLatency { get; set; }

    public bool IsConnected => State == ConnectionState.Connected;

    public bool IsFailed => State == ConnectionState.Failed;

    public bool HasLogTail => !string.IsNullOrEmpty(LogTail);

    public bool HasNoLogTail => !HasLogTail;

    /// <summary>Ошибка подключения с логом ядра — ссылка «Подробности — в журнале».</summary>
    public bool ShowsLogLink => IsFailed && HasLogTail;

    public bool HasNoProfiles => Profiles.Count == 0;

    public bool HasProfiles => Profiles.Count > 0;

    /// <summary>Выбрана группа «Авто» (самый быстрый профиль) вместо одного профиля.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleConnectionCommand))]
    public partial bool IsAutoSelected { get; set; }

    /// <summary>Вторая строка «Авто»: ход подбора, текущий профиль или как работает.</summary>
    [ObservableProperty]
    public partial string AutoSummary { get; set; } = string.Empty;

    /// <summary>Задержка профиля, к которому подключено «Авто».</summary>
    [ObservableProperty]
    public partial string? AutoLatencyText { get; set; }

    /// <summary>«Авто» подключено — отметка в строке, как у активного профиля.</summary>
    [ObservableProperty]
    public partial bool IsAutoActive { get; set; }

    [ObservableProperty]
    public partial bool SortByLatency { get; set; }

    /// <summary>«Служба KHORS (режим TUN): работает, версия …».</summary>
    [ObservableProperty]
    public partial string ServiceStatusText { get; set; } = Localizer.Format("ServiceLabelFormat", Localizer.Get("ServiceState_Unknown"));

    /// <summary>Состояние службы без подписи: «работает, версия …», «не установлена».</summary>
    [ObservableProperty]
    public partial string ServiceStateText { get; set; } = Localizer.Get("ServiceState_Unknown");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveServiceCommand))]
    public partial bool CanInstallService { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveServiceCommand))]
    public partial bool CanRemoveService { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallServiceCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveServiceCommand))]
    public partial bool IsServiceBusy { get; set; }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Режим TUN (весь трафик через службу) вместо системного прокси. Меняется, пока не подключено.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProxyMode))]
    public partial bool IsTunMode { get; set; }

    public bool IsProxyMode
    {
        get => !IsTunMode;
        set => IsTunMode = !value;
    }

    /// <summary>Служба запущена и ответила на рукопожатие — режим TUN можно выбрать.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectTun))]
    public partial bool IsServiceRunning { get; set; }

    /// <summary>Режим меняется только без подключения (переключение на лету — ROADMAP 3.7).</summary>
    public bool CanChangeMode => State is ConnectionState.Disconnected or ConnectionState.Failed && !_auto.IsActive;

    public bool CanSelectTun => CanChangeMode && (IsServiceRunning || IsTunMode);

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
        _auto.StatusChanged -= OnAutoStatusChanged;
        _auto.Measured -= OnAutoMeasured;
    }

    [RelayCommand(CanExecute = nameof(CanToggleConnection))]
    private async Task ToggleConnectionAsync()
    {
        if (_auto.IsActive)
        {
            await _auto.StopAsync(disconnect: true).ConfigureAwait(true);
            return;
        }

        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            await _connection.DisconnectAsync().ConfigureAwait(true);
            return;
        }

        Message = null;
        if (IsAutoSelected)
        {
            _auto.RecheckInterval = AutoRecheckInterval();
            await _auto.StartAsync().ConfigureAwait(true);
            return;
        }

        if (SelectedProfile is null)
        {
            return;
        }

        // Итог прошлого запуска «Авто» не должен описывать ошибку обычного подключения.
        await _auto.StopAsync(disconnect: false).ConfigureAwait(true);
        await _connection.ConnectAsync(SelectedProfile.Profile, CoreStartPreferences.From(_settings.Current)).ConfigureAwait(true);
    }

    private bool CanToggleConnection() =>
        State is ConnectionState.Connected or ConnectionState.Connecting
        || _auto.IsActive
        || (State is ConnectionState.Disconnected or ConnectionState.Failed && (SelectedProfile is not null || (IsAutoSelected && HasProfiles)));

    [RelayCommand]
    private void SelectAuto() => IsAutoSelected = true;

    [RelayCommand(CanExecute = nameof(CanRunInstallService))]
    private Task InstallServiceAsync() => RunServiceSetupAsync(ServiceSetupAction.Install);

    [RelayCommand(CanExecute = nameof(CanRunRemoveService))]
    private Task RemoveServiceAsync() => RunServiceSetupAsync(ServiceSetupAction.Uninstall);

    private bool CanRunInstallService() => CanInstallService && !IsServiceBusy;

    private bool CanRunRemoveService() => CanRemoveService && !IsServiceBusy;

    /// <summary>Установка и удаление — отдельным процессом с повышением прав; окно остаётся без прав администратора.</summary>
    private async Task RunServiceSetupAsync(ServiceSetupAction action)
    {
        IsServiceBusy = true;
        try
        {
            var result = await _service.RunElevatedSetupAsync(action, CancellationToken.None).ConfigureAwait(true);
            Message = result is ServiceSetupResult.Cancelled or ServiceSetupResult.SetupNotFound
                ? Localizer.Get($"ServiceSetup_{result}")
                : Localizer.Get($"ServiceSetup_{action}_{result}");
        }
        finally
        {
            IsServiceBusy = false;
        }

        await RefreshServiceStatusAsync().ConfigureAwait(true);
    }

    /// <summary>Состояние службы; если запущена — рукопожатие по IPC (версия, тот ли процесс на другом конце канала).</summary>
    private async Task RefreshServiceStatusAsync()
    {
        var state = await Task.Run(_service.GetState).ConfigureAwait(true);
        var text = Localizer.Get($"ServiceState_{state}");
        var mismatch = false;
        string? serviceVersion = null;
        if (state == ServiceState.Running)
        {
            try
            {
                await using var client = await IpcClient.ConnectAsync(_serviceTransport, AppVersion, TimeSpan.FromSeconds(3)).ConfigureAwait(true);
                serviceVersion = client.Service.ServiceVersion;
                text = Localizer.Format("ServiceRunningFormat", serviceVersion);
            }
            catch (IpcVersionMismatchException)
            {
                text = Localizer.Get("ServiceVersionMismatch");
                mismatch = true;
            }
            catch (UnauthorizedAccessException)
            {
                text = Localizer.Get("ServiceUntrusted");
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or IpcDisconnectedException or IpcProtocolException or OperationCanceledException)
            {
                text = Localizer.Get("ServiceNotResponding");
            }
        }

        ServiceStatusText = Localizer.Format("ServiceLabelFormat", text);
        ServiceStateText = text;
        IsServiceRunning = serviceVersion is not null;
        CanInstallService = state == ServiceState.NotInstalled || mismatch;
        CanRemoveService = state is not ServiceState.NotInstalled and not ServiceState.Unknown;
    }

    private static string AppVersion => App.AppVersion;

    private TimeSpan AutoRecheckInterval() => TimeSpan.FromMinutes(Math.Clamp(_settings.Current.AutoRecheckMinutes, 1, 1440));

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

    /// <summary>QR-коды со всех мониторов (окно KHORS на время снимка прячется).</summary>
    [RelayCommand]
    private async Task ImportFromScreenAsync()
    {
        ScreenImage? image;
        try
        {
            image = await _dialogs.CaptureScreenAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            image = null;
        }

        if (image is null)
        {
            Message = Localizer.Get("ScreenCaptureFailed");
            return;
        }

        var codes = await Task.Run(() => QrCodes.Decode(image.Bgra, image.Width, image.Height)).ConfigureAwait(true);
        if (codes.Count == 0)
        {
            Message = Localizer.Get("ScreenNoQr");
            return;
        }

        await ImportTextAsync(string.Join('\n', codes)).ConfigureAwait(true);
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

        // С выбранным «Авто» новый профиль просто становится ещё одним кандидатом.
        if (result.Added.Count > 0 && !IsAutoSelected)
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
                    ClearProblemIfConnectionWorks(item.Id, result);
                }
                finally
                {
                    parallel.Release();
                }
            })).ConfigureAwait(true);

            if (SortByLatency)
            {
                ReloadProfiles();
            }
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

    private Uri LatencyUrl() => LatencyTester.TestUrlOrDefault(_settings.Current.LatencyTestUrl);

    /// <summary>После подключения — один замер через текущее ядро.</summary>
    private async Task MeasureConnectionAsync(ConnectionStatus status)
    {
        if (status is not { HttpPort: { } port, Profile: { } profile })
        {
            return;
        }

        ConnectionLatency = Localizer.Get("LatencyTesting");
        var result = await LatencyTester.MeasureThroughProxyAsync(port, LatencyUrl(), LatencyTester.DefaultTimeout).ConfigureAwait(true);

        // Пока шёл замер, могли отключиться или переключиться.
        if (_connection.Status is { State: ConnectionState.Connected, Profile: { } current } && current.Id == profile.Id)
        {
            ConnectionLatency = Localizer.Describe(result);
            _latency[profile.Id] = result;
            Profiles.FirstOrDefault(p => p.Id == profile.Id)?.SetLatency(result);
            ClearProblemIfConnectionWorks(profile.Id, result);
        }
    }

    // Запрос через подключённый профиль прошёл — прежняя причина из лога устарела.
    private void ClearProblemIfConnectionWorks(Guid profileId, LatencyResult result)
    {
        if (result.Status == LatencyStatus.Ok && _connection.Status is { State: ConnectionState.Connected, Profile: { } current } && current.Id == profileId)
        {
            _connection.ClearProblem();
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
            await _connection.ConnectAsync(updated, CoreStartPreferences.From(_settings.Current)).ConfigureAwait(true);
        }
    }

    public Task DeleteAsync(ProfileItemViewModel item) => DeleteProfileAsync(item);

    /// <summary>Звёздочка в строке: отметка сохраняется в профиле и переживает обновление подписки.</summary>
    public Task ToggleFavoriteAsync(ProfileItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_profiles.IsReadOnly)
        {
            Message = Localizer.Get("StorageReadOnly");
            return Task.CompletedTask;
        }

        _profiles.Update(item.Profile with { IsFavorite = !item.Profile.IsFavorite });
        return Task.CompletedTask;
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
        if (value is null)
        {
            return;
        }

        if (value.Id != _settings.Current.SelectedProfileId)
        {
            _settings.Update(s => s with { SelectedProfileId = value.Id });
        }

        // Выбран обычный профиль — «Авто» перестаёт переключать серверы, текущее подключение остаётся.
        IsAutoSelected = false;
        if (_auto.IsActive)
        {
            _ = _auto.StopAsync(disconnect: false);
        }
    }

    partial void OnIsAutoSelectedChanged(bool value)
    {
        if (value)
        {
            SelectedProfile = null;
        }

        if (value != _settings.Current.AutoSelect)
        {
            _settings.Update(s => s with { AutoSelect = value });
        }

        UpdateAutoSummary();
        ApplyStatus(_connection.Status);
    }

    partial void OnIsTunModeChanged(bool value)
    {
        var mode = value ? ConnectionMode.Tun : ConnectionMode.SystemProxy;
        if (mode != _settings.Current.ConnectionMode)
        {
            _settings.Update(s => s with { ConnectionMode = mode });
        }

        OnPropertyChanged(nameof(CanSelectTun));
    }

    partial void OnSortByLatencyChanged(bool value)
    {
        if (value != _settings.Current.SortProfilesByLatency)
        {
            _settings.Update(s => s with { SortProfilesByLatency = value });
        }

        ReloadProfiles();
    }

    private void OnAutoStatusChanged(object? sender, AutoStatus status) => Dispatcher.UIThread.Post(() =>
    {
        UpdateAutoSummary();
        ApplyStatus(_connection.Status);
        ToggleConnectionCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(CanSelectTun));
        if (status.State == AutoState.Connected && SortByLatency)
        {
            ReloadProfiles();
        }
    });

    private void OnAutoMeasured(object? sender, ProfileLatency measured) => Dispatcher.UIThread.Post(() =>
    {
        _latency[measured.ProfileId] = measured.Result;
        Profiles.FirstOrDefault(p => p.Id == measured.ProfileId)?.SetLatency(measured.Result);
        UpdateAutoSummary();
    });

    private void UpdateAutoSummary()
    {
        var auto = _auto.Status;
        var current = auto.ProfileId is { } id ? Profiles.FirstOrDefault(p => p.Id == id) : null;
        AutoSummary = auto switch
        {
            { State: AutoState.Selecting } => Localizer.Format("AutoSelectingFormat", auto.Measured, auto.Total),
            { State: AutoState.Connected } when current is not null => Localizer.Format("AutoCurrentFormat", current.Name),
            { State: AutoState.Waiting } => Localizer.Get("AutoWaitingShort"),
            _ => Localizer.Format("AutoIdleFormat", (int)AutoRecheckInterval().TotalMinutes),
        };
        AutoLatencyText = auto.State == AutoState.Connected && current is { LatencyOk: true } ? current.LatencyText : null;
        IsAutoActive = auto.State is AutoState.Connected or AutoState.Waiting;
    }

    partial void OnStateChanged(ConnectionState value)
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(ShowsLogLink));
        OnPropertyChanged(nameof(CanChangeMode));
        OnPropertyChanged(nameof(CanSelectTun));
    }

    partial void OnLogTailChanged(string? value)
    {
        OnPropertyChanged(nameof(HasLogTail));
        OnPropertyChanged(nameof(HasNoLogTail));
        OnPropertyChanged(nameof(ShowsLogLink));
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));

    private void OnProfilesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(ReloadProfiles);

    private void OnConnectionStatusChanged(object? sender, ConnectionStatus status) => Dispatcher.UIThread.Post(() => ApplyStatus(status));

    private void ReloadProfiles()
    {
        var selectedId = SelectedProfile?.Id;
        Profiles.Clear();
        foreach (var profile in SortByLatency ? _profiles.Profiles.OrderBy(p => LatencyOrder(p.Id)) : _profiles.Profiles.AsEnumerable())
        {
            var item = new ProfileItemViewModel(profile, this);
            if (_latency.TryGetValue(profile.Id, out var latency))
            {
                item.SetLatency(latency);
            }

            Profiles.Add(item);
        }

        SelectedProfile = IsAutoSelected ? null : Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        if (IsAutoSelected && Profiles.Count == 0)
        {
            IsAutoSelected = false;
        }

        ApplyProfileFilter();
        MarkActiveProfile();
        UpdateAutoSummary();
        OnPropertyChanged(nameof(HasNoProfiles));
        OnPropertyChanged(nameof(HasProfiles));

        Subscriptions.Clear();
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in _profiles.Subscriptions)
        {
            Subscriptions.Add(new SubscriptionItemViewModel(subscription, now));
        }

        OnPropertyChanged(nameof(HasSubscriptions));
        OnPropertyChanged(nameof(SubscriptionsHeader));
    }

    // Сортировка по задержке: ответившие по возрастанию, затем непроверенные, затем неответившие (порядок внутри — как в списке).
    private long LatencyOrder(Guid id) => _latency.GetValueOrDefault(id) switch
    {
        { Status: LatencyStatus.Ok, Delay: { } delay } => delay.Ticks,
        null => long.MaxValue - 1,
        _ => long.MaxValue,
    };

    private void ApplyStatus(ConnectionStatus status)
    {
        var justConnected = status.State == ConnectionState.Connected && State != ConnectionState.Connected;
        State = status.State;
        ActiveProfileName = status.Profile?.Name;
        StatusText = Localizer.Get($"Status{status.State}");
        ConnectButtonText = Localizer.Get(status.State is ConnectionState.Connected or ConnectionState.Connecting ? "ButtonDisconnect" : "ButtonConnect");

        StatusDetail = status switch
        {
            { State: ConnectionState.Connected } when IsTunMode => Localizer.Format("TunConnectedFormat", Localizer.CoreName(status.Core)),
            { State: ConnectionState.Connected, HttpPort: { } http, SocksPort: { } socks } => Localizer.Format("LocalProxyFormat", http, socks, Localizer.CoreName(status.Core)),
            { State: ConnectionState.Failed, Failure: { } failure } => Localizer.Describe(failure),
            { State: ConnectionState.Disconnected } when SelectedProfile is null && !IsAutoSelected => Localizer.Get("NoProfileSelected"),
            { State: ConnectionState.Disconnected } => Localizer.Get("HintDisconnected"),
            _ => null,
        };

        ProblemHint = status switch
        {
            { State: ConnectionState.Connected, Problem: { } problem } => Localizer.Format("ConnectionProblemFormat", Localizer.Describe(problem)),
            { State: ConnectionState.Failed, Failure.Problem: { } problem } => Localizer.Describe(problem),
            _ when _auto.Status.State == AutoState.Waiting => Localizer.Get("AutoWaiting"),
            _ => null,
        };

        // «Авто»: подбор до подключения и итог неудачного запуска поверх состояния подключения.
        switch (_auto.Status)
        {
            case { State: AutoState.Selecting } auto when status.State is not ConnectionState.Connected:
                StatusText = Localizer.Get("StatusAutoSelecting");
                StatusDetail = Localizer.Format("AutoSelectingFormat", auto.Measured, auto.Total);
                ConnectButtonText = Localizer.Get("ButtonDisconnect");
                break;
            case { State: AutoState.Failed } when status.State is ConnectionState.Disconnected or ConnectionState.Failed:
                StatusText = Localizer.Get("StatusFailed");
                StatusDetail = status.Failure is { } failure
                    ? Localizer.Get("AutoNoWorkingProfile") + " " + Localizer.Describe(failure)
                    : Localizer.Get("AutoNoWorkingProfile");
                break;
        }

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

        var sessionProfile = status.State == ConnectionState.Connected ? status.Profile : null;
        SessionServer = sessionProfile is null ? null : CountryFlag.Split(sessionProfile.Name).Name;
        SessionProtocol = sessionProfile is null ? null : ProfileItemViewModel.DescribeProtocol(sessionProfile);
        SessionModeCore = sessionProfile is null
            ? null
            : Localizer.Format("SessionModeCoreFormat", Localizer.Get(IsTunMode ? "ModeTunOption" : "ModeProxyShort"), Localizer.CoreName(status.Core));

        if (status.State != ConnectionState.Connected)
        {
            ConnectionLatency = null;
            ResetTraffic();
        }
        else if (justConnected)
        {
            ResetTraffic();
            _ = MeasureConnectionAsync(status);
            _ = PollTrafficAsync();
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
    // Таймер создаётся в UI-потоке: в Avalonia 12 таймер из фонового потока привязан к его диспетчеру
    // и не срабатывает никогда — журнал оставался пустым.
    private void OnLiveLogLine(object? sender, Khors.Engines.Processes.CoreLogLine line)
    {
        if (Interlocked.Exchange(ref _liveLogRefreshQueued, 1) == 0)
        {
            Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(RefreshLiveLog, TimeSpan.FromMilliseconds(500)));
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
            SessionTime = elapsed.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
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

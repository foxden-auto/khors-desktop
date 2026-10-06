using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Latency;

namespace Khors.App.ViewModels;

/// <summary>Профиль в списке: имя, краткое описание подключения, ядро и предупреждение, если есть.</summary>
/// <param name="actions">Команды контекстного меню (меню живёт во всплывающем окне и не видит модель окна).</param>
public sealed partial class ProfileItemViewModel(Profile profile, IProfileActions? actions = null) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public CoreChoice Core { get; } = CoreSelection.Select(profile);

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    private readonly (string? Code, string Name) _flag = CountryFlag.Split(profile.Name);

    /// <summary>Код страны из флага в имени («DE») для плашки; <c>null</c> — флага нет.</summary>
    public string? CountryCode => _flag.Code;

    public bool HasCountryCode => _flag.Code is not null;

    /// <summary>Имя без флага — флаг показан плашкой с кодом страны.</summary>
    public string DisplayName => _flag.Name;

    /// <summary>Например, «Подписка · VLESS · REALITY · TCP · Xray». Названия протоколов не переводятся.</summary>
    public string Summary => string.Join(" · ", new[] { Profile.Group ?? string.Empty, DescribeProtocol(Profile), Localizer.CoreName(Core.Core) }
        .Where(s => s.Length > 0));

    /// <summary>«VLESS · REALITY · TCP»; у Hysteria2/TUIC TLS встроен в QUIC, у WireGuard — свой транспорт: показываем UDP.</summary>
    public static string DescribeProtocol(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return string.Join(" · ", new[]
        {
            ProtocolName(profile.Protocol),
            profile.Protocol.HasOwnTransport ? string.Empty : SecurityName(profile.Security),
            profile.Protocol.HasOwnTransport ? "UDP" : TransportName(profile.Transport),
        }.Where(s => s.Length > 0));
    }

    /// <summary>Сначала — что выбранное ядро не запустит профиль, затем предупреждения валидатора.</summary>
    public string? Warning { get; } = DescribeCore(profile) ?? ProfileValidator.Validate(profile)
        .Where(i => i.Severity == ProfileIssueSeverity.Warning)
        .Select(i => DescribeWarning(profile, i.Code))
        .FirstOrDefault();

    public bool IsCoreAuto => Profile.Core == CorePreference.Auto;

    public bool IsCoreXray => Profile.Core == CorePreference.Xray;

    public bool IsCoreSingBox => Profile.Core == CorePreference.SingBox;

    /// <summary>«Автоматически (сейчас Xray)» — какое ядро выберет автовыбор.</summary>
    public string CoreAutoText => Localizer.Format("CoreAutoFormat", Localizer.CoreName(CoreSelection.For(Profile with { Core = CorePreference.Auto })));

    public bool HasWarning => Warning is not null;

    public bool IsFavorite => Profile.IsFavorite;

    public string FavoriteTooltip => Localizer.Get(IsFavorite ? "FavoriteRemove" : "FavoriteAdd");

    /// <summary>Подходит под строку поиска: имя, код страны, подписка, протокол или ядро.</summary>
    public bool Matches(string query) => MatchesQuery(query, DisplayName, CountryCode, Summary);

    /// <summary>Каждое слово запроса есть хотя бы в одном поле (без учёта регистра); пустой запрос подходит всем.</summary>
    public static bool MatchesQuery(string query, params string?[] fields)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(fields);
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => fields.Any(f => f?.Contains(word, StringComparison.CurrentCultureIgnoreCase) == true));
    }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Итог теста задержки: «123 мс», «тайм-аут», «проверка…»; <c>null</c> — не проверялся.</summary>
    [ObservableProperty]
    public partial string? LatencyText { get; set; }

    [ObservableProperty]
    public partial bool LatencyOk { get; set; }

    /// <summary>Подсказка у задержки: время первого соединения или причина неудачи.</summary>
    [ObservableProperty]
    public partial string? LatencyTooltip { get; set; }

    [ObservableProperty]
    public partial bool LatencyBad { get; set; }

    /// <summary>Уровень задержки для цвета и полосок сигнала: до 150 мс — хорошо, до 400 — средне, дольше — медленно.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLatencyGood), nameof(IsLatencyMid), nameof(IsLatencySlow), nameof(HasLatencyBars))]
    public partial LatencyLevel LatencyLevel { get; set; }

    public bool IsLatencyGood => LatencyLevel == LatencyLevel.Good;

    public bool IsLatencyMid => LatencyLevel == LatencyLevel.Mid;

    public bool IsLatencySlow => LatencyLevel == LatencyLevel.Slow;

    public bool HasLatencyBars => LatencyLevel != LatencyLevel.None;

    public static LatencyLevel LevelOf(LatencyResult? result) => result switch
    {
        { Status: LatencyStatus.Ok, Delay: { } delay } when delay.TotalMilliseconds <= 150 => LatencyLevel.Good,
        { Status: LatencyStatus.Ok, Delay: { } delay } when delay.TotalMilliseconds <= 400 => LatencyLevel.Mid,
        { Status: LatencyStatus.Ok } => LatencyLevel.Slow,
        null => LatencyLevel.None,
        _ => LatencyLevel.Failed,
    };

    public void SetLatency(LatencyResult? result)
    {
        LatencyText = result is null ? Localizer.Get("LatencyTesting") : Localizer.Describe(result);
        LatencyOk = result?.Status == LatencyStatus.Ok;
        LatencyBad = result is not null && result.Status != LatencyStatus.Ok;
        LatencyLevel = LevelOf(result);
        LatencyTooltip = result switch
        {
            { Problem: { } problem } => Localizer.Describe(problem),
            { FirstConnection: { } first } => Localizer.Format("LatencyTooltipFormat", (int)Math.Round(first.TotalMilliseconds)),
            _ => null,
        };
    }

    [RelayCommand]
    private Task SetCoreAsync(CorePreference core) => actions?.SetCoreAsync(this, core) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyLinkAsync() => actions?.CopyLinkAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ShowQrAsync() => actions?.ShowQrAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ToggleFavoriteAsync() => actions?.ToggleFavoriteAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => actions?.DeleteAsync(this) ?? Task.CompletedTask;

    private static string? DescribeCore(Profile profile) => CoreSelection.Select(profile) is { Unsupported: { } field } choice
        ? Localizer.Format("Failure_UnsupportedByCore", Localizer.CoreName(choice.Core), Localizer.DescribeUnsupported(field))
        : null;

    // Имена неизвестных параметров не секретны (секретны значения) — показываем их, чтобы было ясно, что не поддержано.
    private static string DescribeWarning(Profile profile, ProfileIssueCode code) => code == ProfileIssueCode.UnknownParameters
        ? Localizer.Format("ProfileUnknownParamsFormat", string.Join(", ", profile.UnknownParams.Select(p => p.Key).Distinct(StringComparer.Ordinal)))
        : Localizer.Format("ProfileWarningsFormat", Localizer.Describe(code));

    private static string ProtocolName(ProtocolSettings protocol) => protocol switch
    {
        VlessSettings => "VLESS",
        VmessSettings => "VMess",
        TrojanSettings => "Trojan",
        ShadowsocksSettings => "Shadowsocks",
        Hysteria2Settings => "Hysteria2",
        TuicSettings => "TUIC",
        WireGuardSettings => "WireGuard",
        _ => string.Empty,
    };

    private static string SecurityName(SecuritySettings security) => security switch
    {
        TlsSecurity => "TLS",
        RealitySecurity => "REALITY",
        _ => string.Empty,
    };

    private static string TransportName(TransportSettings transport) => transport switch
    {
        TcpTransport => "TCP",
        WsTransport => "WS",
        GrpcTransport => "gRPC",
        HttpUpgradeTransport => "HTTPUpgrade",
        XhttpTransport => "XHTTP",
        _ => string.Empty,
    };
}

/// <summary>Действия над профилем из контекстного меню.</summary>
public interface IProfileActions
{
    Task SetCoreAsync(ProfileItemViewModel item, CorePreference core);

    Task CopyLinkAsync(ProfileItemViewModel item);

    Task ShowQrAsync(ProfileItemViewModel item);

    Task DeleteAsync(ProfileItemViewModel item);

    Task ToggleFavoriteAsync(ProfileItemViewModel item);
}

/// <summary>Уровень задержки профиля: <see cref="None"/> — не проверялся или проверяется.</summary>
public enum LatencyLevel
{
    None,
    Good,
    Mid,
    Slow,
    Failed,
}

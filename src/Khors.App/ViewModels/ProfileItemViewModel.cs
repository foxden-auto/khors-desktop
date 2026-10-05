using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Core.Profiles;
using Khors.Engines.Latency;

namespace Khors.App.ViewModels;

/// <summary>Профиль в списке: имя, краткое описание подключения и предупреждение, если есть.</summary>
public sealed partial class ProfileItemViewModel(Profile profile) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    /// <summary>Например, «Подписка · VLESS · REALITY · TCP». Названия протоколов не переводятся.</summary>
    public string Summary => string.Join(" · ", new[]
    {
        Profile.Group ?? string.Empty,
        ProtocolName(Profile.Protocol),
        // У Hysteria2/TUIC TLS встроен в QUIC, у WireGuard — свой транспорт: показываем UDP.
        Profile.Protocol.HasOwnTransport ? string.Empty : SecurityName(Profile.Security),
        Profile.Protocol.HasOwnTransport ? "UDP" : TransportName(Profile.Transport),
    }.Where(s => s.Length > 0));

    public string? Warning { get; } = ProfileValidator.Validate(profile)
        .Where(i => i.Severity == ProfileIssueSeverity.Warning)
        .Select(i => DescribeWarning(profile, i.Code))
        .FirstOrDefault();

    public bool HasWarning => Warning is not null;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Итог теста задержки: «123 мс», «тайм-аут», «проверка…»; <c>null</c> — не проверялся.</summary>
    [ObservableProperty]
    public partial string? LatencyText { get; set; }

    [ObservableProperty]
    public partial bool LatencyOk { get; set; }

    /// <summary>Подсказка: время первого соединения рядом с задержкой.</summary>
    [ObservableProperty]
    public partial string? LatencyTooltip { get; set; }

    [ObservableProperty]
    public partial bool LatencyBad { get; set; }

    public void SetLatency(LatencyResult? result)
    {
        LatencyText = result is null ? Localizer.Get("LatencyTesting") : Localizer.Describe(result);
        LatencyOk = result?.Status == LatencyStatus.Ok;
        LatencyBad = result is not null && result.Status != LatencyStatus.Ok;
        LatencyTooltip = result?.FirstConnection is { } first
            ? Localizer.Format("LatencyTooltipFormat", (int)Math.Round(first.TotalMilliseconds))
            : null;
    }

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

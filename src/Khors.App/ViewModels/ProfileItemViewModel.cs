using CommunityToolkit.Mvvm.ComponentModel;
using Khors.App.Services;
using Khors.Core.Profiles;

namespace Khors.App.ViewModels;

/// <summary>Профиль в списке: имя, краткое описание подключения и предупреждение, если есть.</summary>
public sealed partial class ProfileItemViewModel(Profile profile) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    /// <summary>Например, «VLESS · REALITY · TCP». Названия протоколов не переводятся.</summary>
    public string Summary => string.Join(" · ", new[] { ProtocolName(Profile.Protocol), SecurityName(Profile.Security), TransportName(Profile.Transport) }.Where(s => s.Length > 0));

    public string? Warning { get; } = ProfileValidator.Validate(profile)
        .Where(i => i.Severity == ProfileIssueSeverity.Warning)
        .Select(i => DescribeWarning(profile, i.Code))
        .FirstOrDefault();

    public bool HasWarning => Warning is not null;

    [ObservableProperty]
    public partial bool IsActive { get; set; }

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

using System.Text;
using System.Text.Json;

namespace Khors.Core.Profiles;

/// <summary>Проверка профиля на ошибки, при которых ядро не запустится или не подключится.</summary>
public static class ProfileValidator
{
    private const int MaxStringIdBytes = 30;
    private const int RealityPublicKeyBytes = 32;
    private const int MaxShortIdLength = 16;

    private static readonly HashSet<string> s_flows = new(StringComparer.Ordinal)
    {
        "xtls-rprx-vision",
        "xtls-rprx-vision-udp443",
    };

    public static IReadOnlyList<ProfileIssue> Validate(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var issues = new List<ProfileIssue>();
        void Error(ProfileIssueCode code, string field) => issues.Add(new(code, ProfileIssueSeverity.Error, field));
        void Warning(ProfileIssueCode code, string field) => issues.Add(new(code, ProfileIssueSeverity.Warning, field));

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            Error(ProfileIssueCode.NameEmpty, "name");
        }

        ValidateServer(profile.Server, Error);
        ValidateProtocol(profile, Error);
        ValidateTransport(profile.Transport, Error);
        ValidateSecurity(profile, Error, Warning);

        if (profile.Mux is { Concurrency: < -1 or > 1024 })
        {
            Error(ProfileIssueCode.MuxConcurrencyOutOfRange, "mux.concurrency");
        }

        if (profile.UnknownParams.Count > 0)
        {
            Warning(ProfileIssueCode.UnknownParameters, "unknownParams");
        }

        return issues;
    }

    private static void ValidateServer(ServerEndpoint server, Action<ProfileIssueCode, string> error)
    {
        if (string.IsNullOrWhiteSpace(server.Host))
        {
            error(ProfileIssueCode.HostEmpty, "server.host");
        }
        else if (server.Host.Any(c => char.IsWhiteSpace(c) || c is '/' or '@' or '[' or ']' or '?' or '#'))
        {
            error(ProfileIssueCode.HostInvalid, "server.host");
        }

        if (server.Port is < 1 or > 65535)
        {
            error(ProfileIssueCode.PortOutOfRange, "server.port");
        }
    }

    private static void ValidateProtocol(Profile profile, Action<ProfileIssueCode, string> error)
    {
        switch (profile.Protocol)
        {
            case VlessSettings vless:
                ValidateUserId(vless.Id, error);
                ValidateFlow(profile, vless, error);
                break;

            case VmessSettings vmess:
                ValidateUserId(vmess.Id, error);
                if (vmess.AlterId < 0)
                {
                    error(ProfileIssueCode.AlterIdNegative, "protocol.alterId");
                }

                break;

            case TrojanSettings trojan when trojan.Password.IsEmpty:
                error(ProfileIssueCode.PasswordEmpty, "protocol.password");
                break;

            case ShadowsocksSettings ss:
                if (string.IsNullOrWhiteSpace(ss.Method))
                {
                    error(ProfileIssueCode.MethodEmpty, "protocol.method");
                }

                if (ss.Password.IsEmpty && ss.Method != "none")
                {
                    error(ProfileIssueCode.PasswordEmpty, "protocol.password");
                }

                break;
        }
    }

    /// <summary>UUID или произвольная строка до 30 байт UTF-8 (Xray отображает её в UUIDv5).</summary>
    private static void ValidateUserId(Secret id, Action<ProfileIssueCode, string> error)
    {
        if (id.IsEmpty)
        {
            error(ProfileIssueCode.IdEmpty, "protocol.id");
        }
        else if (!Guid.TryParseExact(id.Value, "D", out _) && Encoding.UTF8.GetByteCount(id.Value) > MaxStringIdBytes)
        {
            error(ProfileIssueCode.IdInvalid, "protocol.id");
        }
    }

    private static void ValidateFlow(Profile profile, VlessSettings vless, Action<ProfileIssueCode, string> error)
    {
        if (string.IsNullOrEmpty(vless.Flow))
        {
            return;
        }

        if (!s_flows.Contains(vless.Flow))
        {
            error(ProfileIssueCode.FlowUnknown, "protocol.flow");
            return;
        }

        if (profile.Transport is not TcpTransport)
        {
            error(ProfileIssueCode.FlowRequiresTcp, "protocol.flow");
        }

        if (profile.Security is NoSecurity)
        {
            error(ProfileIssueCode.FlowRequiresTlsOrReality, "protocol.flow");
        }

        if (profile.Mux is { Enabled: true })
        {
            error(ProfileIssueCode.MuxIncompatibleWithFlow, "mux.enabled");
        }
    }

    private static void ValidateTransport(TransportSettings transport, Action<ProfileIssueCode, string> error)
    {
        switch (transport)
        {
            case TcpTransport { HeaderType: not ("none" or "http") }:
                error(ProfileIssueCode.TransportModeUnknown, "transport.headerType");
                break;

            case GrpcTransport { Mode: not ("gun" or "multi") }:
                error(ProfileIssueCode.TransportModeUnknown, "transport.mode");
                break;

            case XhttpTransport xhttp:
                if (xhttp.Mode is not ("auto" or "packet-up" or "stream-up" or "stream-one"))
                {
                    error(ProfileIssueCode.TransportModeUnknown, "transport.mode");
                }

                if (xhttp.Extra is not null && !IsJsonObject(xhttp.Extra))
                {
                    error(ProfileIssueCode.XhttpExtraInvalid, "transport.extra");
                }

                break;
        }
    }

    private static void ValidateSecurity(
        Profile profile,
        Action<ProfileIssueCode, string> error,
        Action<ProfileIssueCode, string> warning)
    {
        switch (profile.Security)
        {
            case TlsSecurity { AllowInsecure: true }:
                warning(ProfileIssueCode.InsecureTls, "security.allowInsecure");
                break;

            case RealitySecurity reality:
                if (string.IsNullOrWhiteSpace(reality.Sni))
                {
                    error(ProfileIssueCode.RealitySniEmpty, "security.sni");
                }

                if (!IsRealityPublicKey(reality.PublicKey.Value))
                {
                    error(ProfileIssueCode.RealityPublicKeyInvalid, "security.publicKey");
                }

                if (reality.ShortId is { } shortId && !IsShortId(shortId.Value))
                {
                    error(ProfileIssueCode.RealityShortIdInvalid, "security.shortId");
                }

                // REALITY в Xray работает поверх RAW(TCP), gRPC и XHTTP.
                if (profile.Transport is WsTransport or HttpUpgradeTransport)
                {
                    error(ProfileIssueCode.RealityTransportUnsupported, "transport");
                }

                break;
        }
    }

    private static bool IsRealityPublicKey(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        Span<byte> buffer = stackalloc byte[48];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written == RealityPublicKeyBytes;
    }

    private static bool IsShortId(string value) =>
        value.Length <= MaxShortIdLength && value.Length % 2 == 0 && value.All(char.IsAsciiHexDigit);

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

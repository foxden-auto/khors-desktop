using System.Text;
using System.Text.Json;

namespace Khors.Core.Profiles;

/// <summary>Проверка профиля на ошибки, при которых ядро не запустится или не подключится.</summary>
public static class ProfileValidator
{
    private const int MaxStringIdBytes = 30;
    private const int RealityPublicKeyBytes = 32;
    private const int MaxShortIdLength = 16;
    private const int MlDsa65PublicKeyBytes = 1952;
    private const int Sha256Bytes = 32;
    private const int WireGuardKeyBytes = 32;

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
        if (!profile.Protocol.HasOwnTransport)
        {
            ValidateTransport(profile.Transport, Error);
        }

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

            case Hysteria2Settings hysteria:
                if (hysteria.Password.IsEmpty)
                {
                    error(ProfileIssueCode.PasswordEmpty, "protocol.password");
                }

                if (hysteria.Obfs is not null && (hysteria.Obfs != "salamander" || hysteria.ObfsPassword is not { IsEmpty: false }))
                {
                    error(ProfileIssueCode.ObfsInvalid, "protocol.obfs");
                }

                if (hysteria.Ports is not null && !IsPortList(hysteria.Ports))
                {
                    error(ProfileIssueCode.PortsInvalid, "protocol.ports");
                }

                RequireQuicTls(profile, error);
                break;

            case TuicSettings tuic:
                if (!Guid.TryParseExact(tuic.Uuid.Value, "D", out _))
                {
                    error(tuic.Uuid.IsEmpty ? ProfileIssueCode.IdEmpty : ProfileIssueCode.IdInvalid, "protocol.uuid");
                }

                if (tuic.Password.IsEmpty)
                {
                    error(ProfileIssueCode.PasswordEmpty, "protocol.password");
                }

                if (tuic.CongestionControl is not ("cubic" or "new_reno" or "bbr") || tuic.UdpRelayMode is not ("native" or "quic"))
                {
                    error(ProfileIssueCode.TuicModeUnknown, "protocol");
                }

                RequireQuicTls(profile, error);
                break;

            case WireGuardSettings wireGuard:
                if (DecodedLength(wireGuard.PrivateKey.Value) != WireGuardKeyBytes
                    || DecodedLength(wireGuard.PeerPublicKey.Value) != WireGuardKeyBytes
                    || (wireGuard.PreSharedKey is { } psk && DecodedLength(psk.Value) != WireGuardKeyBytes))
                {
                    error(ProfileIssueCode.WireGuardKeyInvalid, "protocol");
                }

                if (wireGuard.LocalAddresses.Count == 0 || !wireGuard.LocalAddresses.All(IsCidr))
                {
                    error(ProfileIssueCode.WireGuardAddressInvalid, "protocol.localAddresses");
                }

                if (wireGuard.Reserved.Count is not (0 or 3) || wireGuard.Reserved.Any(b => b is < 0 or > 255))
                {
                    error(ProfileIssueCode.WireGuardReservedInvalid, "protocol.reserved");
                }

                if (wireGuard.Mtu is < 576 or > 65535)
                {
                    error(ProfileIssueCode.MtuOutOfRange, "protocol.mtu");
                }

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
            case TlsSecurity tls:
                if (tls.AllowInsecure)
                {
                    warning(ProfileIssueCode.InsecureTls, "security.allowInsecure");
                }

                if (tls.PinnedPeerCertSha256.Any(h => !IsSha256Hex(h)))
                {
                    error(ProfileIssueCode.TlsPinnedCertInvalid, "security.pinnedPeerCertSha256");
                }

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

                if (reality.MlDsa65Verify is { } pqv && DecodedLength(pqv) != MlDsa65PublicKeyBytes)
                {
                    error(ProfileIssueCode.RealityMlDsa65VerifyInvalid, "security.mlDsa65Verify");
                }

                if (reality.ShortId is { } shortId && !IsShortId(shortId.Value))
                {
                    error(ProfileIssueCode.RealityShortIdInvalid, "security.shortId");
                }

                if (reality.SupportsX25519MlKem768 == true && profile.Core == CorePreference.SingBox)
                {
                    error(ProfileIssueCode.RealityPostQuantumRequiresXray, "core");
                }

                // REALITY в Xray работает поверх RAW(TCP), gRPC и XHTTP.
                if (profile.Transport is WsTransport or HttpUpgradeTransport)
                {
                    error(ProfileIssueCode.RealityTransportUnsupported, "transport");
                }

                break;
        }
    }

    private static bool IsRealityPublicKey(string value) => DecodedLength(value) == RealityPublicKeyBytes;

    /// <summary>Длина base64/base64url-значения в байтах; -1, если это не base64.</summary>
    private static int DecodedLength(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/').TrimEnd('=');
        base64 += new string('=', (4 - (base64.Length % 4)) % 4);
        var buffer = new byte[base64.Length];
        return Convert.TryFromBase64String(base64, buffer, out var written) ? written : -1;
    }

    /// <summary>Hysteria2 и TUIC работают поверх QUIC — только с TLS.</summary>
    private static void RequireQuicTls(Profile profile, Action<ProfileIssueCode, string> error)
    {
        if (profile.Security is not TlsSecurity)
        {
            error(ProfileIssueCode.QuicRequiresTls, "security");
        }
    }

    /// <summary>Список портов и диапазонов: <c>443</c>, <c>443,20000-30000</c>.</summary>
    private static bool IsPortList(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries).All(part =>
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            return range.Length is 1 or 2
                && range.All(p => int.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
                && (range.Length == 1 || int.Parse(range[0], System.Globalization.CultureInfo.InvariantCulture) <= int.Parse(range[1], System.Globalization.CultureInfo.InvariantCulture));
        });

    private static bool IsCidr(string value)
    {
        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || !System.Net.IPAddress.TryParse(value[..slash], out var ip)
            || !int.TryParse(value[(slash + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        return prefix <= (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32);
    }

    private static bool IsSha256Hex(string value)
    {
        var hex = value.Replace(":", string.Empty, StringComparison.Ordinal);
        return hex.Length == Sha256Bytes * 2 && hex.All(char.IsAsciiHexDigit);
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

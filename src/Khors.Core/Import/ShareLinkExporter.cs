using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Khors.Core.Profiles;

namespace Khors.Core.Import;

/// <summary>
/// Профиль → ссылка (docs/SPEC.md, 4.2: экспорт в ссылку и QR). Обратное преобразование к <see cref="ShareLinkParser"/>:
/// разбор экспортированной ссылки даёт тот же профиль (кроме выбора ядра, мультиплекса и служебных полей).
/// Чистая функция. Ссылка содержит секреты профиля — в лог не пишется.
/// </summary>
/// <remarks>
/// VMess — в формате v2rayN (base64 JSON), который понимает большинство клиентов; если в профиле есть то, чего в нём
/// нет (REALITY, <c>pcs</c>, <c>vcn</c>), и alterId = 0 — в стандарте ссылок Xray. Shadowsocks — SIP002.
/// Неизвестные параметры возвращаются в ссылку, кроме путей из конфигов подписок (<c>smux.enabled</c> и т. п.).
/// </remarks>
public static partial class ShareLinkExporter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Export(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Protocol switch
        {
            VlessSettings vless => Standard("vless", vless.Id.Value, profile, Query()
                .Add("encryption", vless.Encryption)
                .Add("flow", vless.Flow)),
            TrojanSettings trojan => Standard("trojan", trojan.Password.Value, profile, Query()),
            VmessSettings vmess => Vmess(profile, vmess),
            ShadowsocksSettings ss => Shadowsocks(profile, ss),
            Hysteria2Settings hysteria => Hysteria2(profile, hysteria),
            TuicSettings tuic => Tuic(profile, tuic),
            WireGuardSettings wireGuard => WireGuard(profile, wireGuard),
            _ => throw new NotSupportedException(profile.Protocol.GetType().Name),
        };
    }

    private static QueryBuilder Query() => new();

    /// <summary>Ссылка в стиле Xray: <c>scheme://userinfo@host:port?…транспорт…&amp;security=…#имя</c>.</summary>
    private static string Standard(string scheme, string userinfo, Profile profile, QueryBuilder query)
    {
        AddTransport(query, profile.Transport);
        AddSecurity(query, profile.Security);
        return Link(scheme, Escape(userinfo), HostPort(profile.Server.Host, profile.Server.Port.ToString(CultureInfo.InvariantCulture)), query, profile);
    }

    private static void AddTransport(QueryBuilder query, TransportSettings transport)
    {
        switch (transport)
        {
            case TcpTransport tcp:
                query.Add("type", "tcp");
                if (tcp.HeaderType != "none")
                {
                    query.Add("headerType", tcp.HeaderType).Add("host", tcp.Host).Add("path", tcp.Path);
                }

                break;
            case WsTransport ws:
                query.Add("type", "ws").Add("path", ws.Path).Add("host", ws.Host);
                break;
            case HttpUpgradeTransport upgrade:
                query.Add("type", "httpupgrade").Add("path", upgrade.Path).Add("host", upgrade.Host);
                break;
            case GrpcTransport grpc:
                query.Add("type", "grpc").Add("serviceName", grpc.ServiceName).Add("mode", grpc.Mode).Add("authority", grpc.Authority);
                break;
            case XhttpTransport xhttp:
                query.Add("type", "xhttp").Add("path", xhttp.Path).Add("host", xhttp.Host).Add("mode", xhttp.Mode).Add("extra", xhttp.Extra);
                break;
        }
    }

    private static void AddSecurity(QueryBuilder query, SecuritySettings security)
    {
        switch (security)
        {
            case NoSecurity:
                query.Add("security", "none");
                break;
            case TlsSecurity tls:
                query.Add("security", "tls")
                    .Add("sni", tls.Sni)
                    .Add("alpn", Join(tls.Alpn))
                    .Add("fp", tls.Fingerprint)
                    .Add("allowInsecure", tls.AllowInsecure ? "1" : null)
                    .Add("pcs", Join(tls.PinnedPeerCertSha256))
                    .Add("vcn", Join(tls.VerifyPeerCertByName));
                break;
            case RealitySecurity reality:
                query.Add("security", "reality")
                    .Add("sni", reality.Sni)
                    .Add("fp", reality.Fingerprint)
                    .Add("pbk", reality.PublicKey.Value)
                    .Add("sid", reality.ShortId?.Value)
                    .Add("spx", reality.SpiderX)
                    .Add("pqv", reality.MlDsa65Verify)
                    .Add("support-x25519mlkem768", reality.SupportsX25519MlKem768 is { } pq ? (pq ? "true" : "false") : null);
                break;
        }
    }

    private static string Vmess(Profile profile, VmessSettings vmess)
    {
        var needsStandard = profile.Security is RealitySecurity
            || profile.Security is TlsSecurity { PinnedPeerCertSha256.Count: > 0 } or TlsSecurity { VerifyPeerCertByName.Count: > 0 };
        if (needsStandard && vmess.AlterId == 0)
        {
            return Standard("vmess", vmess.Id.Value, profile, Query().Add("encryption", vmess.Cipher));
        }

        // v2rayN: значения — строки; net — транспорт, type — заголовок TCP или режим gRPC/XHTTP.
        var json = new JsonObject
        {
            ["v"] = "2",
            ["ps"] = profile.Name,
            ["add"] = profile.Server.Host,
            ["port"] = profile.Server.Port.ToString(CultureInfo.InvariantCulture),
            ["id"] = vmess.Id.Value,
            ["aid"] = vmess.AlterId.ToString(CultureInfo.InvariantCulture),
            ["scy"] = vmess.Cipher,
        };

        void Set(string key, string? value)
        {
            if (value is not null)
            {
                json[key] = value;
            }
        }

        switch (profile.Transport)
        {
            case TcpTransport tcp:
                Set("net", "tcp");
                Set("type", tcp.HeaderType);
                if (tcp.HeaderType == "http")
                {
                    Set("host", tcp.Host);
                    Set("path", tcp.Path);
                }

                break;
            case WsTransport ws:
                Set("net", "ws");
                Set("host", ws.Host);
                Set("path", ws.Path);
                break;
            case HttpUpgradeTransport upgrade:
                Set("net", "httpupgrade");
                Set("host", upgrade.Host);
                Set("path", upgrade.Path);
                break;
            case GrpcTransport grpc:
                Set("net", "grpc");
                Set("type", grpc.Mode);
                Set("host", grpc.Authority);
                Set("path", grpc.ServiceName);
                break;
            case XhttpTransport xhttp:
                Set("net", "xhttp");
                Set("type", xhttp.Mode);
                Set("host", xhttp.Host);
                Set("path", xhttp.Path);
                Set("extra", xhttp.Extra);
                break;
        }

        if (profile.Security is TlsSecurity tls)
        {
            Set("tls", "tls");
            Set("sni", tls.Sni);
            Set("alpn", Join(tls.Alpn));
            Set("fp", tls.Fingerprint);
            Set("allowInsecure", tls.AllowInsecure ? "1" : null);
        }
        else
        {
            Set("tls", "none");
        }

        foreach (var param in ExportableUnknown(profile))
        {
            if (!json.ContainsKey(param.Key))
            {
                json[param.Key] = param.Value;
            }
        }

        return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString(s_jsonOptions)));
    }

    /// <summary>SIP002: методы 2022 — открытым текстом с %-кодированием, остальные — base64url без паддинга.</summary>
    private static string Shadowsocks(Profile profile, ShadowsocksSettings ss)
    {
        var credentials = $"{ss.Method}:{ss.Password.Value}";
        var userinfo = ss.Method.StartsWith("2022-", StringComparison.Ordinal)
            ? Escape(ss.Method) + ":" + Escape(ss.Password.Value)
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var query = Query().Add("plugin", ss.Plugin is null ? null : ss.PluginOptions is null ? ss.Plugin : $"{ss.Plugin};{ss.PluginOptions}");
        var hostPort = HostPort(profile.Server.Host, profile.Server.Port.ToString(CultureInfo.InvariantCulture));
        return Link("ss", userinfo, hostPort + (query.IsEmpty && !HasExportableUnknown(profile) ? string.Empty : "/"), query, profile);
    }

    private static string Hysteria2(Profile profile, Hysteria2Settings hysteria)
    {
        var tls = profile.Security as TlsSecurity ?? new TlsSecurity();
        var query = Query()
            .Add("sni", tls.Sni)
            .Add("alpn", Join(tls.Alpn))
            .Add("insecure", tls.AllowInsecure ? "1" : null)
            .Add("pinSHA256", Join(tls.PinnedPeerCertSha256))
            .Add("obfs", hysteria.Obfs)
            .Add("obfs-password", hysteria.ObfsPassword?.Value)
            .Add("hop_interval", Number(hysteria.HopIntervalSeconds))
            .Add("upmbps", Number(hysteria.UpMbps))
            .Add("downmbps", Number(hysteria.DownMbps));

        // Порты для смены порта — в адресе (схема ссылок Hysteria 2): «хост:443,20000-30000».
        var ports = hysteria.Ports ?? profile.Server.Port.ToString(CultureInfo.InvariantCulture);
        var userinfo = hysteria.Password.Value.Length > 0 ? Escape(hysteria.Password.Value) : null;
        return Link("hysteria2", userinfo, HostPort(profile.Server.Host, ports) + "/", query, profile);
    }

    private static string Tuic(Profile profile, TuicSettings tuic)
    {
        var tls = profile.Security as TlsSecurity ?? new TlsSecurity();
        var query = Query()
            .Add("congestion_control", tuic.CongestionControl)
            .Add("udp_relay_mode", tuic.UdpRelayMode)
            .Add("reduce_rtt", tuic.ZeroRttHandshake ? "1" : null)
            .Add("sni", tls.Sni)
            .Add("alpn", Join(tls.Alpn))
            .Add("allow_insecure", tls.AllowInsecure ? "1" : null);

        var userinfo = Escape(tuic.Uuid.Value) + ":" + Escape(tuic.Password.Value);
        return Link("tuic", userinfo, HostPort(profile.Server.Host, profile.Server.Port.ToString(CultureInfo.InvariantCulture)), query, profile);
    }

    private static string WireGuard(Profile profile, WireGuardSettings wireGuard)
    {
        var query = Query()
            .Add("publickey", wireGuard.PeerPublicKey.Value)
            .Add("presharedkey", wireGuard.PreSharedKey?.Value)
            .Add("address", Join(wireGuard.LocalAddresses))
            .Add("reserved", wireGuard.Reserved.Count > 0 ? string.Join(',', wireGuard.Reserved.Select(b => b.ToString(CultureInfo.InvariantCulture))) : null)
            .Add("mtu", Number(wireGuard.Mtu));

        return Link("wireguard", Escape(wireGuard.PrivateKey.Value), HostPort(profile.Server.Host, profile.Server.Port.ToString(CultureInfo.InvariantCulture)), query, profile);
    }

    private static string Link(string scheme, string? userinfo, string hostPort, QueryBuilder query, Profile profile)
    {
        foreach (var param in ExportableUnknown(profile))
        {
            query.AddRaw(param.Key, param.Value);
        }

        var builder = new StringBuilder(scheme).Append("://");
        if (userinfo is not null)
        {
            builder.Append(userinfo).Append('@');
        }

        builder.Append(hostPort);
        if (!query.IsEmpty)
        {
            builder.Append('?').Append(query);
        }

        return builder.Append('#').Append(Escape(profile.Name)).ToString();
    }

    private static string HostPort(string host, string port) =>
        (host.Contains(':', StringComparison.Ordinal) ? $"[{host}]" : host) + ":" + port;

    /// <summary>Неизвестные параметры ссылки. Пути из конфигов подписок (с точкой) в ссылку не попадают.</summary>
    private static IEnumerable<UnknownParam> ExportableUnknown(Profile profile) =>
        profile.UnknownParams.Where(p => LinkParameterName().IsMatch(p.Key));

    private static bool HasExportableUnknown(Profile profile) => ExportableUnknown(profile).Any();

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex LinkParameterName();

    private static string? Join(EquatableArray<string> values) => values.Count > 0 ? string.Join(',', values) : null;

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class QueryBuilder
    {
        private readonly List<string> _parts = [];

        public bool IsEmpty => _parts.Count == 0;

        /// <summary>Параметр со значением; <c>null</c> — не добавляется.</summary>
        public QueryBuilder Add(string key, string? value)
        {
            if (value is not null)
            {
                _parts.Add(Escape(key) + "=" + Escape(value));
            }

            return this;
        }

        /// <summary>Неизвестный параметр как есть, в том числе без значения.</summary>
        public void AddRaw(string key, string value) =>
            _parts.Add(value.Length == 0 ? Escape(key) : Escape(key) + "=" + Escape(value));

        public override string ToString() => string.Join('&', _parts);
    }
}

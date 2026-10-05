using System.Text.Json.Nodes;
using Khors.Core.Import;
using Khors.Core.Profiles;

namespace Khors.Core.Subscriptions;

/// <summary>
/// Подписка — конфиг Xray или массив конфигов с <c>remarks</c> (так отдают JSON-подписки v2rayN / 3x-ui).
/// Профили — из прокси-выходов (vless, vmess, trojan, shadowsocks); служебные (freedom, blackhole, dns…) пропускаются.
/// Поля — по документации Xray-core; поддерживаются и vnext/servers, и плоская запись settings.
/// </summary>
internal static class XrayJson
{
    private static readonly HashSet<string> s_proxies = new(StringComparer.Ordinal) { "vless", "vmess", "trojan", "shadowsocks" };

    public static bool IsXray(JsonNode root) => root switch
    {
        JsonObject single => HasXrayOutbounds(single),
        JsonArray configs => configs.Count > 0 && configs.All(c => c is JsonObject config && HasXrayOutbounds(config)),
        _ => false,
    };

    public static void Parse(JsonNode root, List<Profile> profiles, List<ImportLineError> errors)
    {
        var configs = root is JsonArray array ? array.OfType<JsonObject>().ToList() : [(JsonObject)root];
        var index = 0;
        foreach (var config in configs)
        {
            var remarks = config["remarks"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            var outbounds = config["outbounds"]!.AsArray().OfType<JsonObject>()
                .Where(o => o["protocol"]?.GetValue<string>() is { } protocol && protocol != "freedom" && protocol != "blackhole" && protocol != "dns" && protocol != "loopback")
                .ToList();

            foreach (var outbound in outbounds)
            {
                index++;
                try
                {
                    var name = outbounds.Count == 1 ? remarks : remarks is null ? null : $"{remarks} ({outbound["tag"]})";
                    profiles.Add(ParseOutbound(new JsonFields(outbound), name));
                }
                catch (LinkFormatException ex)
                {
                    errors.Add(new ImportLineError(index, new LinkParseError(ex.Code, ex.Field)));
                }
            }
        }
    }

    private static bool HasXrayOutbounds(JsonObject config) =>
        config["outbounds"] is JsonArray outbounds && outbounds.OfType<JsonObject>().Any(o => o["protocol"] is not null);

    private static Profile ParseOutbound(JsonFields outbound, string? remarks)
    {
        var protocolName = outbound.String("protocol") ?? string.Empty;
        if (!s_proxies.Contains(protocolName))
        {
            throw new LinkFormatException(LinkParseErrorCode.UnsupportedScheme, "protocol");
        }

        var tag = outbound.String("tag");
        var settings = outbound.Object("settings") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "settings");

        // vnext[0] / servers[0] — классическая запись; иначе плоская (address, port, id прямо в settings).
        var server = settings.FirstOf("vnext") ?? settings.FirstOf("servers") ?? settings;
        var user = server.FirstOf("users") ?? server;
        var host = server.String("address") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "address");
        var port = server.Int("port") is { } p and >= 1 and <= 65535 ? p : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "port");
        user.Ignore("level", "email");

        ProtocolSettings protocol = protocolName switch
        {
            "vless" => new VlessSettings
            {
                Id = new Secret(user.String("id") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "id")),
                Flow = user.String("flow"),
                Encryption = user.String("encryption") ?? "none",
            },
            "vmess" => new VmessSettings
            {
                Id = new Secret(user.String("id") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "id")),
                AlterId = user.Int("alterId") ?? 0,
                Cipher = user.String("security") ?? "auto",
            },
            "trojan" => new TrojanSettings
            {
                Password = new Secret(server.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
            },
            _ => new ShadowsocksSettings
            {
                Method = server.String("method") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "method"),
                Password = new Secret(server.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
            },
        };

        var stream = outbound.Object("streamSettings");
        var name = remarks ?? tag;
        return new Profile
        {
            Id = Guid.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? LinkUrl.NameOrAddress(null, host, port) : name.Trim(),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Transport = Transport(stream),
            Security = Security(stream),
            Mux = outbound.Object("mux") is { } mux && mux.Bool("enabled") == true
                ? new MuxSettings { Enabled = true, Concurrency = mux.Int("concurrency") }
                : null,
            UnknownParams = new EquatableArray<UnknownParam>(outbound.Remaining()),
        };
    }

    private static TransportSettings Transport(JsonFields? stream)
    {
        var network = (stream?.String("network") ?? "tcp").ToLowerInvariant();
        switch (network)
        {
            case "tcp" or "raw":
                var header = (stream?.Object("rawSettings") ?? stream?.Object("tcpSettings"))?.Object("header");
                if (header?.String("type") == "http")
                {
                    var request = header.Object("request");
                    return new TcpTransport
                    {
                        HeaderType = "http",
                        Path = request?.Strings("path").FirstOrDefault(),
                        Host = request?.Object("headers")?.Strings("Host").FirstOrDefault(),
                    };
                }

                return new TcpTransport();

            case "ws":
                var ws = stream!.Object("wsSettings");
                return new WsTransport { Path = ws?.String("path") ?? "/", Host = ws?.String("host") ?? ws?.Object("headers")?.String("Host") };

            case "httpupgrade":
                var upgrade = stream!.Object("httpupgradeSettings");
                return new HttpUpgradeTransport { Path = upgrade?.String("path") ?? "/", Host = upgrade?.String("host") };

            case "grpc":
                var grpc = stream!.Object("grpcSettings");
                return new GrpcTransport
                {
                    ServiceName = grpc?.String("serviceName") ?? string.Empty,
                    Mode = grpc?.Bool("multiMode") == true ? "multi" : "gun",
                    Authority = grpc?.String("authority"),
                };

            case "xhttp" or "splithttp":
                var xhttp = stream!.Object("xhttpSettings") ?? stream.Object("splithttpSettings");
                return new XhttpTransport
                {
                    Path = xhttp?.String("path") ?? "/",
                    Host = xhttp?.String("host"),
                    Mode = xhttp?.String("mode") ?? "auto",
                    Extra = xhttp?.Raw("extra"),
                };

            default:
                throw new LinkFormatException(LinkParseErrorCode.UnsupportedTransport, "network");
        }
    }

    private static SecuritySettings Security(JsonFields? stream)
    {
        switch (stream?.String("security") ?? "none")
        {
            case "none":
                return new NoSecurity();

            case "tls":
                var tls = stream!.Object("tlsSettings");
                return new TlsSecurity
                {
                    Sni = tls?.String("serverName"),
                    Alpn = new EquatableArray<string>(tls?.Strings("alpn") ?? []),
                    Fingerprint = tls?.String("fingerprint"),
                    AllowInsecure = tls?.Bool("allowInsecure") ?? false,
                    PinnedPeerCertSha256 = new EquatableArray<string>(SplitComma(tls?.String("pinnedPeerCertSha256"))),
                    VerifyPeerCertByName = new EquatableArray<string>(SplitComma(tls?.String("verifyPeerCertByName"))),
                };

            case "reality":
                var reality = stream!.Object("realitySettings");
                return new RealitySecurity
                {
                    Sni = reality?.String("serverName") ?? string.Empty,
                    Fingerprint = reality?.String("fingerprint") ?? RealitySecurity.DefaultFingerprint,
                    // Xray 26 принимает ключ и как publicKey, и как password.
                    PublicKey = new Secret(reality?.String("publicKey") ?? reality?.String("password") ?? string.Empty),
                    ShortId = reality?.String("shortId") is { } shortId ? new Secret(shortId) : null,
                    SpiderX = reality?.String("spiderX"),
                    MlDsa65Verify = reality?.String("mldsa65Verify"),
                };

            default:
                throw new LinkFormatException(LinkParseErrorCode.UnsupportedSecurity, "security");
        }
    }

    private static string[] SplitComma(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
}

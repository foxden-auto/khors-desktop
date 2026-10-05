using System.Globalization;
using System.Text.Json.Nodes;
using Khors.Core.Import;
using Khors.Core.Profiles;

namespace Khors.Core.Subscriptions;

/// <summary>
/// Подписка — конфиг sing-box: профили из прокси-выходов <c>outbounds</c> (поля — по документации sing-box).
/// Служебные выходы (direct, block, dns, selector, urltest) пропускаются.
/// </summary>
internal static class SingBoxJson
{
    private static readonly HashSet<string> s_serviceTypes = new(StringComparer.Ordinal) { "direct", "block", "dns", "selector", "urltest" };

    public static bool IsSingBox(JsonObject root) =>
        root["outbounds"] is JsonArray outbounds && outbounds.OfType<JsonObject>().Any(o => o["type"] is not null && o["protocol"] is null);

    public static void Parse(JsonObject root, List<Profile> profiles, List<ImportLineError> errors)
    {
        var index = 0;
        foreach (var node in root["outbounds"]!.AsArray())
        {
            index++;
            if (node is not JsonObject outbound || outbound["type"]?.GetValue<string>() is not { } type || s_serviceTypes.Contains(type))
            {
                continue;
            }

            try
            {
                profiles.Add(ParseOutbound(type, new JsonFields(outbound)));
            }
            catch (LinkFormatException ex)
            {
                errors.Add(new ImportLineError(index, new LinkParseError(ex.Code, ex.Field)));
            }
        }
    }

    private static Profile ParseOutbound(string type, JsonFields fields)
    {
        fields.Ignore("type");
        var name = fields.String("tag");
        var host = fields.String("server") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "server");
        var port = fields.Int("server_port") is { } p and >= 1 and <= 65535 ? p : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "server_port");
        fields.Ignore("packet_encoding", "network");

        ProtocolSettings protocol = type switch
        {
            "vless" => new VlessSettings
            {
                Id = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
                Flow = fields.String("flow"),
            },
            "vmess" => new VmessSettings
            {
                Id = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
                AlterId = fields.Int("alter_id") ?? 0,
                Cipher = fields.String("security") ?? "auto",
            },
            "trojan" => new TrojanSettings
            {
                Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
            },
            "shadowsocks" => new ShadowsocksSettings
            {
                Method = fields.String("method") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "method"),
                Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
                Plugin = fields.String("plugin"),
                PluginOptions = fields.String("plugin_opts"),
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedScheme, "type"),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? LinkUrl.NameOrAddress(null, host, port) : name.Trim(),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Transport = Transport(fields.Object("transport")),
            Security = Security(fields.Object("tls")),
            Mux = fields.Object("multiplex") is { } mux && mux.Bool("enabled") == true
                ? new MuxSettings { Enabled = true, Concurrency = mux.Int("max_connections") }
                : null,
            UnknownParams = new EquatableArray<UnknownParam>(fields.Remaining()),
        };
    }

    private static TransportSettings Transport(JsonFields? transport)
    {
        if (transport is null)
        {
            return new TcpTransport();
        }

        switch (transport.String("type"))
        {
            case "ws":
                var path = transport.String("path") ?? "/";
                var host = transport.Object("headers")?.String("Host");
                if (transport.Int("max_early_data") is > 0 and var earlyData
                    && (transport.String("early_data_header_name") ?? "Sec-WebSocket-Protocol") == "Sec-WebSocket-Protocol")
                {
                    path += (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "ed=" + earlyData.ToString(CultureInfo.InvariantCulture);
                }

                return new WsTransport { Path = path, Host = host };

            case "httpupgrade":
                return new HttpUpgradeTransport { Path = transport.String("path") ?? "/", Host = transport.String("host") };

            case "grpc":
                return new GrpcTransport { ServiceName = transport.String("service_name") ?? string.Empty };

            default:
                throw new LinkFormatException(LinkParseErrorCode.UnsupportedTransport, "transport.type");
        }
    }

    private static SecuritySettings Security(JsonFields? tls)
    {
        if (tls is null || tls.Bool("enabled") != true)
        {
            return new NoSecurity();
        }

        var sni = tls.String("server_name");
        var fingerprint = tls.Object("utls") is { } utls && utls.Bool("enabled") == true ? utls.String("fingerprint") : null;
        var alpn = tls.Strings("alpn");
        var insecure = tls.Bool("insecure") ?? false;

        if (tls.Object("reality") is { } reality && reality.Bool("enabled") == true)
        {
            return new RealitySecurity
            {
                Sni = sni ?? string.Empty,
                Fingerprint = fingerprint ?? RealitySecurity.DefaultFingerprint,
                PublicKey = new Secret(reality.String("public_key") ?? string.Empty),
                ShortId = reality.String("short_id") is { } shortId ? new Secret(shortId) : null,
            };
        }

        return new TlsSecurity { Sni = sni, Fingerprint = fingerprint, AllowInsecure = insecure, Alpn = new EquatableArray<string>(alpn) };
    }
}

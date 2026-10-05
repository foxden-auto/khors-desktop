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

        // WireGuard в sing-box 1.11+ — раздел endpoints.
        IEnumerable<JsonNode?> nodes = root["outbounds"]!.AsArray();
        if (root["endpoints"] is JsonArray endpoints)
        {
            nodes = nodes.Concat(endpoints);
        }

        foreach (var node in nodes)
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
        if (type == "wireguard")
        {
            return WireGuard(fields, name);
        }

        var host = fields.String("server") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "server");
        // Hysteria2 с диапазоном портов: server_ports «a:b» вместо server_port.
        var serverPorts = type == "hysteria2" ? fields.Strings("server_ports") : [];
        var port = fields.Int("server_port") is { } p and >= 1 and <= 65535
            ? p
            : serverPorts.Length > 0 && int.TryParse(serverPorts[0].Split(':')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first) ? first
            : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "server_port");
        fields.Ignore("packet_encoding", "network");
        var obfs = type == "hysteria2" ? fields.Object("obfs") : null;

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
            "hysteria2" => new Hysteria2Settings
            {
                Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
                Obfs = obfs?.String("type"),
                ObfsPassword = obfs?.String("password") is { } obfsPassword ? new Secret(obfsPassword) : null,
                Ports = serverPorts.Length > 0 ? string.Join(',', serverPorts.Select(r => r.Replace(':', '-'))) : null,
                HopIntervalSeconds = Seconds(fields.String("hop_interval")),
                UpMbps = fields.Int("up_mbps"),
                DownMbps = fields.Int("down_mbps"),
            },
            "tuic" => new TuicSettings
            {
                Uuid = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
                Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
                CongestionControl = fields.String("congestion_control") ?? "cubic",
                UdpRelayMode = fields.String("udp_relay_mode") ?? "native",
                ZeroRttHandshake = fields.Bool("zero_rtt_handshake") ?? false,
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedScheme, "type"),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? LinkUrl.NameOrAddress(null, host, port) : name.Trim(),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Transport = protocol.HasOwnTransport ? new TcpTransport() : Transport(fields.Object("transport")),
            Security = Security(fields.Object("tls")),
            Mux = fields.Object("multiplex") is { } mux && mux.Bool("enabled") == true
                ? new MuxSettings { Enabled = true, Concurrency = mux.Int("max_connections") }
                : null,
            UnknownParams = new EquatableArray<UnknownParam>(fields.Remaining()),
        };
    }

    /// <summary>WireGuard: endpoint sing-box 1.11+ (address, peers[0]) или устаревший outbound (server, local_address).</summary>
    private static Profile WireGuard(JsonFields fields, string? name)
    {
        var peer = fields.FirstOf("peers");
        var host = peer?.String("address") ?? fields.String("server") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "server");
        var port = (peer?.Int("port") ?? fields.Int("server_port")) is { } p and >= 1 and <= 65535 ? p : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "port");
        peer?.Ignore("allowed_ips", "persistent_keepalive_interval");
        fields.Ignore("system", "name", "listen_port", "workers");

        var publicKey = peer?.String("public_key") ?? fields.String("peer_public_key");
        var preSharedKey = peer?.String("pre_shared_key") ?? fields.String("pre_shared_key");
        var reserved = (peer is not null ? peer.Strings("reserved") : fields.Strings("reserved"))
            .Select(b => int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1);
        var addresses = fields.Strings("address") is { Length: > 0 } endpointAddresses ? endpointAddresses : fields.Strings("local_address");

        var protocol = new WireGuardSettings
        {
            PrivateKey = new Secret(fields.String("private_key") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "private_key")),
            PeerPublicKey = new Secret(publicKey ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "public_key")),
            PreSharedKey = preSharedKey is null ? null : new Secret(preSharedKey),
            LocalAddresses = new EquatableArray<string>(addresses),
            Reserved = new EquatableArray<int>(reserved),
            Mtu = fields.Int("mtu"),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? LinkUrl.NameOrAddress(null, host, port) : name.Trim(),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            UnknownParams = new EquatableArray<UnknownParam>(fields.Remaining()),
        };
    }

    /// <summary>«30s», «2m» → секунды.</summary>
    private static int? Seconds(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
        {
            return null;
        }

        var number = new string(duration.TakeWhile(char.IsAsciiDigit).ToArray());
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return duration[number.Length..] switch { "m" => value * 60, "h" => value * 3600, _ => value };
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

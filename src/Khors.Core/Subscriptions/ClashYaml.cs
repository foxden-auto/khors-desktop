using System.Globalization;
using Khors.Core.Import;
using Khors.Core.Profiles;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Khors.Core.Subscriptions;

/// <summary>
/// Подписка в формате Clash / mihomo: YAML с разделом <c>proxies</c>. Поля — по документации mihomo.
/// YAML читается как дерево узлов, без десериализации в типы. Неизвестные поля прокси сохраняются
/// в <see cref="Profile.UnknownParams"/> с путём через точку (<c>ws-opts.max-early-data</c>).
/// </summary>
internal static class ClashYaml
{
    /// <returns><c>false</c> — это не YAML Clash (нет раздела <c>proxies</c>).</returns>
    public static bool TryParse(string text, out List<Profile> profiles, out List<ImportLineError> errors)
    {
        profiles = [];
        errors = [];

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException)
        {
            return false;
        }

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root
            || !root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxiesNode)
            || proxiesNode is not YamlSequenceNode proxies)
        {
            return false;
        }

        var index = 0;
        foreach (var node in proxies)
        {
            index++;
            if (node is not YamlMappingNode proxy)
            {
                errors.Add(new ImportLineError(index, new LinkParseError(LinkParseErrorCode.Malformed, null)));
                continue;
            }

            try
            {
                profiles.Add(ParseProxy(new Fields(proxy, prefix: string.Empty)));
            }
            catch (LinkFormatException ex)
            {
                errors.Add(new ImportLineError(index, new LinkParseError(ex.Code, ex.Field)));
            }
        }

        return true;
    }

    private static Profile ParseProxy(Fields fields)
    {
        var type = (fields.String("type") ?? string.Empty).ToLowerInvariant();
        var name = fields.String("name");
        var host = fields.String("server") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "server");
        var port = fields.Int("port") is { } p and >= 1 and <= 65535 ? p : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "port");
        fields.Take("udp"); // Xray и sing-box передают UDP и так.

        ProtocolSettings protocol = type switch
        {
            "vless" => new VlessSettings
            {
                Id = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
                Flow = fields.String("flow"),
                Encryption = fields.String("encryption") ?? "none",
            },
            "vmess" => new VmessSettings
            {
                Id = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
                AlterId = fields.Int("alterId") ?? 0,
                Cipher = fields.String("cipher") ?? "auto",
            },
            "trojan" => new TrojanSettings
            {
                Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
            },
            "ss" => Shadowsocks(fields),
            "hysteria2" => Hysteria2(fields),
            "tuic" => Tuic(fields),
            "wireguard" => WireGuard(fields),
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedScheme, "type"),
        };

        // Hysteria2 и TUIC — QUIC (TLS всегда, транспорт свой), WireGuard — без TLS, Shadowsocks — без транспорта.
        var transport = protocol is ShadowsocksSettings || protocol.HasOwnTransport ? new TcpTransport() : Transport(fields);
        SecuritySettings security = protocol switch
        {
            ShadowsocksSettings or WireGuardSettings => new NoSecurity(),
            Hysteria2Settings or TuicSettings => QuicTls(fields),
            _ => Security(fields, defaultTls: protocol is TrojanSettings),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = string.IsNullOrWhiteSpace(name) ? LinkUrl.NameOrAddress(null, host, port) : name.Trim(),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Transport = transport,
            Security = security,
            UnknownParams = new EquatableArray<UnknownParam>(fields.Remaining()),
        };
    }

    private static ShadowsocksSettings Shadowsocks(Fields fields)
    {
        var settings = new ShadowsocksSettings
        {
            Method = (fields.String("cipher") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "cipher")).ToLowerInvariant(),
            Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
        };

        var plugin = fields.String("plugin");
        if (plugin is null)
        {
            return settings;
        }

        // mihomo: obfs → obfs-local (SIP003), остальные плагины — под своим именем; plugin-opts → «ключ=значение;…».
        var options = fields.Map("plugin-opts")?.AllScalars() ?? [];
        var sip003 = plugin == "obfs"
            ? options.Select(o => o.Key switch { "mode" => $"obfs={o.Value}", "host" => $"obfs-host={o.Value}", _ => $"{o.Key}={o.Value}" })
            : options.Select(o => o.Value == "true" ? o.Key : $"{o.Key}={o.Value}");
        return settings with
        {
            Plugin = plugin == "obfs" ? "obfs-local" : plugin,
            PluginOptions = string.Join(';', sip003) is { Length: > 0 } joined ? joined : null,
        };
    }

    private static Hysteria2Settings Hysteria2(Fields fields) => new()
    {
        Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
        Obfs = fields.String("obfs"),
        ObfsPassword = fields.String("obfs-password") is { } obfsPassword ? new Secret(obfsPassword) : null,
        Ports = fields.String("ports"),
        HopIntervalSeconds = fields.Int("hop-interval"),
        UpMbps = Mbps(fields.String("up")),
        DownMbps = Mbps(fields.String("down")),
    };

    /// <summary>TUIC v5 (uuid + password); v4 (token) не поддерживается.</summary>
    private static TuicSettings Tuic(Fields fields) => new()
    {
        Uuid = new Secret(fields.String("uuid") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "uuid")),
        Password = new Secret(fields.String("password") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "password")),
        CongestionControl = fields.String("congestion-controller") ?? "cubic",
        UdpRelayMode = fields.String("udp-relay-mode") ?? "native",
        ZeroRttHandshake = fields.Bool("reduce-rtt") ?? false,
    };

    private static WireGuardSettings WireGuard(Fields fields)
    {
        var addresses = new List<string>();
        if (fields.String("ip") is { } ipv4)
        {
            addresses.Add(ipv4.Contains('/', StringComparison.Ordinal) ? ipv4 : ipv4 + "/32");
        }

        if (fields.String("ipv6") is { } ipv6)
        {
            addresses.Add(ipv6.Contains('/', StringComparison.Ordinal) ? ipv6 : ipv6 + "/128");
        }

        return new WireGuardSettings
        {
            PrivateKey = new Secret(fields.String("private-key") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "private-key")),
            PeerPublicKey = new Secret(fields.String("public-key") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "public-key")),
            PreSharedKey = fields.String("pre-shared-key") is { } psk ? new Secret(psk) : null,
            LocalAddresses = new EquatableArray<string>(addresses),
            Reserved = new EquatableArray<int>(fields.Strings("reserved").Select(b => int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1)),
            Mtu = fields.Int("mtu"),
        };
    }

    /// <summary>TLS для QUIC: sni, alpn, skip-cert-verify; fingerprint mihomo — SHA-256 сертификата (закрепление).</summary>
    private static TlsSecurity QuicTls(Fields fields)
    {
        fields.Take("disable-sni");
        return new TlsSecurity
        {
            Sni = fields.String("sni") ?? fields.String("servername"),
            Alpn = new EquatableArray<string>(fields.Strings("alpn")),
            AllowInsecure = fields.Bool("skip-cert-verify") ?? false,
            PinnedPeerCertSha256 = new EquatableArray<string>(fields.Strings("fingerprint")),
        };
    }

    /// <summary>«100», «100 Mbps» → 100.</summary>
    private static int? Mbps(string? value)
    {
        var digits = new string((value ?? string.Empty).Trim().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    private static TransportSettings Transport(Fields fields)
    {
        var network = (fields.String("network") ?? "tcp").ToLowerInvariant();
        switch (network)
        {
            case "tcp":
                return new TcpTransport();

            // Маскировка TCP под HTTP/1.1.
            case "http":
                var http = fields.Map("http-opts");
                return new TcpTransport { HeaderType = "http", Path = http?.Strings("path").FirstOrDefault(), Host = http?.Map("headers")?.Strings("Host").FirstOrDefault() };

            case "ws":
                var ws = fields.Map("ws-opts");
                var path = ws?.String("path") ?? "/";
                var wsHost = ws?.Map("headers")?.String("Host");
                if (ws?.Bool("v2ray-http-upgrade") == true)
                {
                    return new HttpUpgradeTransport { Path = path, Host = wsHost };
                }

                // Early data mihomo (заголовок Sec-WebSocket-Protocol) — то же, что «?ed=» в пути у Xray.
                if (ws?.Int("max-early-data") is > 0 and var earlyData
                    && (ws.String("early-data-header-name") ?? "Sec-WebSocket-Protocol") == "Sec-WebSocket-Protocol")
                {
                    path += (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "ed=" + earlyData.ToString(CultureInfo.InvariantCulture);
                }

                return new WsTransport { Path = path, Host = wsHost };

            case "grpc":
                return new GrpcTransport { ServiceName = fields.Map("grpc-opts")?.String("grpc-service-name") ?? string.Empty };

            case "xhttp":
                var xhttp = fields.Map("xhttp-opts");
                return new XhttpTransport { Path = xhttp?.String("path") ?? "/", Host = xhttp?.String("host"), Mode = xhttp?.String("mode") ?? "auto" };

            default:
                throw new LinkFormatException(LinkParseErrorCode.UnsupportedTransport, "network");
        }
    }

    private static SecuritySettings Security(Fields fields, bool defaultTls)
    {
        var sni = fields.String("servername") ?? fields.String("sni");
        var fingerprint = fields.String("client-fingerprint");
        var insecure = fields.Bool("skip-cert-verify") ?? false;
        var alpn = fields.Strings("alpn");
        var tls = fields.Bool("tls") ?? defaultTls;

        if (fields.Map("reality-opts") is { } reality)
        {
            return new RealitySecurity
            {
                Sni = sni ?? string.Empty,
                Fingerprint = fingerprint ?? RealitySecurity.DefaultFingerprint,
                PublicKey = new Secret(reality.String("public-key") ?? string.Empty),
                ShortId = reality.String("short-id") is { } shortId ? new Secret(shortId) : null,
                SupportsX25519MlKem768 = reality.Bool("support-x25519mlkem768"),
            };
        }

        return tls
            ? new TlsSecurity { Sni = sni, Fingerprint = fingerprint, AllowInsecure = insecure, Alpn = new EquatableArray<string>(alpn) }
            : new NoSecurity();
    }

    /// <summary>Поля прокси: чтение с отметкой «использовано»; остальное — в неизвестные параметры.</summary>
    private sealed class Fields(YamlMappingNode node, string prefix)
    {
        private readonly HashSet<string> _taken = new(StringComparer.Ordinal);
        private readonly List<Fields> _children = [];

        public YamlNode? Take(string key)
        {
            _taken.Add(key);
            return node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
        }

        public string? String(string key) => Take(key) is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

        public int? Int(string key) =>
            int.TryParse(String(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

        public bool? Bool(string key) => String(key)?.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };

        public string[] Strings(string key) => Take(key) switch
        {
            YamlSequenceNode sequence => [.. sequence.OfType<YamlScalarNode>().Select(s => s.Value).OfType<string>()],
            YamlScalarNode { Value: { Length: > 0 } single } => [single],
            _ => [],
        };

        public Fields? Map(string key)
        {
            if (Take(key) is not YamlMappingNode map)
            {
                return null;
            }

            var child = new Fields(map, prefix + key + ".");
            _children.Add(child);
            return child;
        }

        /// <summary>Все скалярные поля (для plugin-opts, где ключи произвольные).</summary>
        public List<KeyValuePair<string, string>> AllScalars()
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in node.Children)
            {
                if (key is YamlScalarNode { Value: { } name } && value is YamlScalarNode { Value: { } text })
                {
                    _taken.Add(name);
                    result.Add(new(name, text));
                }
            }

            return result;
        }

        public IEnumerable<UnknownParam> Remaining()
        {
            foreach (var (key, value) in node.Children)
            {
                if (key is YamlScalarNode { Value: { } name } && !_taken.Contains(name))
                {
                    yield return new UnknownParam(prefix + name, value is YamlScalarNode scalar ? scalar.Value ?? string.Empty : value.ToString());
                }
            }

            foreach (var child in _children)
            {
                foreach (var param in child.Remaining())
                {
                    yield return param;
                }
            }
        }
    }
}

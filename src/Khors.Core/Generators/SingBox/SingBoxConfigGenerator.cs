using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Khors.Core.Dns;
using Khors.Core.Profiles;

namespace Khors.Core.Generators.SingBox;

/// <summary>Параметры запуска sing-box, не относящиеся к профилю.</summary>
public sealed record SingBoxConfigOptions
{
    public string ListenAddress { get; init; } = "127.0.0.1";

    public required int SocksPort { get; init; }

    public required int HttpPort { get; init; }

    /// <summary>Уровень лога в терминах Xray (debug, info, warning, error, none) — переводится в уровни sing-box.</summary>
    public string LogLevel { get; init; } = "warning";

    /// <summary>Режим TUN (весь трафик системы); <c>null</c> — только локальные входы (режим «Системный прокси»).</summary>
    public SingBoxTunOptions? Tun { get; init; }

    /// <summary>
    /// Цепочка TUN → SOCKS → Xray (docs/SPEC.md, 3.3): выход — локальный SOCKS-вход Xray на этом порту,
    /// протокол профиля обрабатывает Xray. <c>null</c> — выход из профиля.
    /// </summary>
    public int? UpstreamSocksPort { get; init; }

    /// <summary>
    /// Счётчики трафика: Clash API на loopback (<c>/connections</c> — uploadTotal/downloadTotal); <c>null</c> — выключены.
    /// API умеет и управлять ядром, поэтому без секрета не включается.
    /// </summary>
    public ClashApiOptions? ClashApi { get; init; }

    /// <summary>
    /// Удалённый DNS для имён сайтов: в режиме TUN и для WireGuard (через WireGuard идут IP-пакеты, имена разрешает
    /// клиент). Запросы идут через сервер прокси, к адресу в локальной сети — напрямую (ROADMAP 3.4).
    /// </summary>
    public DnsServer RemoteDns { get; init; } = DnsPresets.Default.Server;
}

/// <param name="Secret">Случайный на каждый запуск; конфиг уходит ядру через stdin и на диск не пишется.</param>
public sealed record ClashApiOptions(int Port, string Secret)
{
    // Секрет — в журнал не выводим.
    public override string ToString() => $"ClashApiOptions {{ Port = {Port} }}";
}

/// <summary>Адаптер TUN sing-box (wintun на Windows).</summary>
public sealed record SingBoxTunOptions
{
    public string InterfaceName { get; init; } = "KHORS";

    // Не адреса по умолчанию sing-box (172.18/172.19.0.1, fdfe:dcba:9876::1): их же берут другие клиенты на sing-box
    // (Throne и др.), и два адаптера с одной подсетью конфликтуют. 198.19.0.0/16 — тестовый диапазон (RFC 2544)
    // вне 198.18.0.0/16 fake-ip Clash, в локальных сетях не встречается; IPv6 — свой ULA.
    public string Inet4Address { get; init; } = "198.19.75.1/30";

    public string Inet6Address { get; init; } = "fd4b:6873:72b5::1/126";

    public int Mtu { get; init; } = 9000;

    /// <summary>
    /// Адреса (IP или подсеть), которые идут мимо туннеля: адрес сервера профиля, к которому подключается Xray
    /// в цепочке, — иначе его трафик снова попадёт в TUN (петля).
    /// </summary>
    public IReadOnlyList<string> ExcludeAddresses { get; init; } = [];
}

/// <summary>
/// Генерация конфига sing-box из профиля: входы SOCKS и HTTP на 127.0.0.1, выход (или endpoint WireGuard)
/// из профиля, локальные сети напрямую. В режиме TUN (<see cref="SingBoxConfigOptions.Tun"/>) — ещё адаптер TUN
/// с <c>auto_route</c>/<c>strict_route</c>, перехват DNS и выход sing-box мимо туннеля (<c>auto_detect_interface</c>);
/// в цепочке (<see cref="SingBoxConfigOptions.UpstreamSocksPort"/>) выход — SOCKS-вход Xray. JSON строится только
/// из модели (CLAUDE.md, правило 3). Формат — по документации sing-box 1.14.
/// </summary>
public static class SingBoxConfigGenerator
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CoreConfigResult Generate(Profile profile, SingBoxConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        // В цепочке протокол профиля обрабатывает Xray — возможности sing-box для него не важны.
        var chained = options.UpstreamSocksPort is not null;
        if (profile.Core == CorePreference.Xray && !chained)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.WrongCore, "core");
        }

        if (ProfileValidator.Validate(profile).FirstOrDefault(i => i.Severity == ProfileIssueSeverity.Error) is { } issue)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.ProfileInvalid, issue.Field);
        }

        if (!chained && FindUnsupported(profile) is { } field)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.UnsupportedFeature, field);
        }

        var config = new JsonObject
        {
            ["log"] = Log(options.LogLevel),
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" }),
            },
            ["inbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["tag"] = "socks-in", ["listen"] = options.ListenAddress, ["listen_port"] = options.SocksPort },
                new JsonObject { ["type"] = "http", ["tag"] = "http-in", ["listen"] = options.ListenAddress, ["listen_port"] = options.HttpPort }),
        };

        // WireGuard в sing-box 1.11+ — endpoint, остальные протоколы — outbound.
        if (options.UpstreamSocksPort is { } upstream)
        {
            config["outbounds"] = new JsonArray(
                new JsonObject { ["type"] = "socks", ["tag"] = ProxyTag, ["server"] = "127.0.0.1", ["server_port"] = upstream, ["version"] = "5" },
                new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
        }
        else if (profile.Protocol is WireGuardSettings wireGuard)
        {
            // Через WireGuard идут IP-пакеты: имена сайтов разрешаются на клиенте. Системный резолвер
            // недоступен через туннель (сервер обычно режет частные адреса), поэтому DNS — удалённый из настроек, через туннель.
            AddRemoteDns(config, options.RemoteDns);
            config["endpoints"] = new JsonArray(WireGuardEndpoint(profile, wireGuard));
            config["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
        }
        else
        {
            config["outbounds"] = new JsonArray(ProxyOutbound(profile), new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
        }

        config["route"] = new JsonObject
        {
            ["rules"] = new JsonArray(
                new JsonObject { ["action"] = "sniff" },
                new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag }),
            ["final"] = ProxyTag,
            // Адрес сервера-домена разрешается системным резолвером.
            ["default_domain_resolver"] = "local",
        };

        if (options.Tun is { } tun)
        {
            AddTun(config, tun, options.RemoteDns);
        }

        if (options.ClashApi is { } clashApi)
        {
            config["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"{options.ListenAddress}:{clashApi.Port}",
                    ["secret"] = clashApi.Secret,
                },
            };
        }

        return CoreConfigResult.Success(config.ToJsonString(s_jsonOptions));
    }

    private const string TunTag = "tun-in";
    private const string RemoteDnsTag = "remote";

    /// <summary>
    /// TUN: весь трафик системы — в sing-box. Сам sing-box выходит в сеть через физический интерфейс
    /// (<c>auto_detect_interface</c>), иначе его соединения с сервером снова попали бы в TUN. DNS-запросы системы
    /// перехватываются и уходят через туннель на удалённый DNS из настроек, чтобы провайдер их не видел, и отдают
    /// только IPv4-адреса.
    /// </summary>
    private static void AddTun(JsonObject config, SingBoxTunOptions tun, DnsServer remoteDns)
    {
        var inbound = new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = TunTag,
            ["interface_name"] = tun.InterfaceName,
            ["address"] = new JsonArray(tun.Inet4Address, tun.Inet6Address),
            ["mtu"] = tun.Mtu,
            ["auto_route"] = true,
            ["strict_route"] = true,
        };
        if (tun.ExcludeAddresses.Count > 0)
        {
            inbound["route_exclude_address"] = new JsonArray([.. tun.ExcludeAddresses.Select(a => (JsonNode)JsonValue.Create(ToPrefix(a))!)]);
        }

        config["inbounds"]!.AsArray().Insert(0, inbound);

        var dns = config["dns"]!.AsObject();

        // Приложениям — только IPv4-адреса: у адаптера есть IPv6 (чтобы IPv6 не шёл мимо туннеля), и Windows
        // предпочла бы IPv6, а у многих серверов нет выхода в IPv6 — соединения браузера к таким адресам умирали.
        // На запросы AAAA sing-box отвечает пустым ответом без ошибки.
        dns["strategy"] = "ipv4_only";
        if (dns["final"] is null)
        {
            AddRemoteDns(config, remoteDns);
        }

        var route = config["route"]!.AsObject();
        route["rules"]!.AsArray().Insert(1, new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        route["auto_detect_interface"] = true;
    }

    /// <summary>Адрес без маски — как подсеть из одного адреса (/32 или /128).</summary>
    private static string ToPrefix(string address) =>
        address.Contains('/', StringComparison.Ordinal) ? address : address + (address.Contains(':', StringComparison.Ordinal) ? "/128" : "/32");

    /// <summary>
    /// Удалённый DNS — главный (<c>final</c>). Имя самого DNS-сервера разрешает системный резолвер (<c>local</c>),
    /// иначе сервер ждал бы сам себя; адрес в локальной сети — напрямую, остальные — через сервер прокси.
    /// </summary>
    private static void AddRemoteDns(JsonObject config, DnsServer server)
    {
        var node = new JsonObject
        {
            ["type"] = server.Type switch
            {
                DnsServerType.Udp => "udp",
                DnsServerType.Tcp => "tcp",
                DnsServerType.Tls => "tls",
                _ => "https",
            },
            ["tag"] = RemoteDnsTag,
            ["server"] = server.Host,
        };
        if (server.Port is { } port)
        {
            node["server_port"] = port;
        }

        if (server.Path is { } path)
        {
            node["path"] = path;
        }

        if (!server.HostIsAddress)
        {
            node["domain_resolver"] = "local";
        }

        if (!server.IsLocalNetwork)
        {
            node["detour"] = ProxyTag;
        }

        var dns = config["dns"]!.AsObject();
        dns["servers"]!.AsArray().Add(node);
        dns["final"] = RemoteDnsTag;
    }

    /// <summary>
    /// Возможность профиля, которой нет в sing-box 1.14: поле профиля или <c>null</c>, если sing-box его запустит
    /// (без учёта ошибок профиля и выбора ядра).
    /// </summary>
    public static string? FindUnsupported(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        switch (profile.Protocol)
        {
            case VlessSettings { Encryption: not "none" }:
                return "protocol.encryption";
            case ShadowsocksSettings { Plugin: not null and not ("obfs-local" or "v2ray-plugin") }:
                return "protocol.plugin";
        }

        if (!profile.Protocol.HasOwnTransport)
        {
            switch (profile.Transport)
            {
                case TcpTransport { HeaderType: "http" }:
                    return "transport.headerType";
                case XhttpTransport:
                    return "transport";
                case GrpcTransport { Mode: "multi" }:
                    return "transport.mode";
            }
        }

        switch (profile.Security)
        {
            // sing-box закрепляет хэш открытого ключа, а pcs/pinSHA256 — хэш сертификата: перевести одно в другое нельзя.
            case TlsSecurity { PinnedPeerCertSha256.Count: > 0 }:
                return "security.pinnedPeerCertSha256";
            case TlsSecurity { VerifyPeerCertByName.Count: > 0 }:
                return "security.verifyPeerCertByName";

            // sing-box не отправляет X25519MLKEM768 в ClientHello REALITY (CLAUDE.md, «Предметные знания»).
            case RealitySecurity { SupportsX25519MlKem768: true }:
                return "security.supportsX25519MlKem768";
            case RealitySecurity { MlDsa65Verify: not null }:
                return "security.mlDsa65Verify";
        }

        return null;
    }

    private static JsonObject Log(string level) => level switch
    {
        "none" => new JsonObject { ["disabled"] = true },
        _ => new JsonObject
        {
            ["level"] = level switch { "debug" => "debug", "info" => "info", "error" => "error", _ => "warn" },
            ["timestamp"] = true,
        },
    };

    private static JsonObject ProxyOutbound(Profile profile)
    {
        var outbound = new JsonObject
        {
            ["type"] = Type(profile.Protocol),
            ["tag"] = ProxyTag,
            ["server"] = profile.Server.Host,
        };

        switch (profile.Protocol)
        {
            case VlessSettings vless:
                outbound["server_port"] = profile.Server.Port;
                outbound["uuid"] = vless.Id.Value;
                if (!string.IsNullOrEmpty(vless.Flow))
                {
                    outbound["flow"] = vless.Flow;
                }

                // Совместимо с Xray: UDP поверх VLESS через XUDP.
                outbound["packet_encoding"] = "xudp";
                break;

            case VmessSettings vmess:
                outbound["server_port"] = profile.Server.Port;
                outbound["uuid"] = vmess.Id.Value;
                outbound["security"] = vmess.Cipher;
                outbound["alter_id"] = vmess.AlterId;
                break;

            case TrojanSettings trojan:
                outbound["server_port"] = profile.Server.Port;
                outbound["password"] = trojan.Password.Value;
                break;

            case ShadowsocksSettings ss:
                outbound["server_port"] = profile.Server.Port;
                outbound["method"] = ss.Method;
                outbound["password"] = ss.Password.Value;
                if (ss.Plugin is not null)
                {
                    outbound["plugin"] = ss.Plugin;
                    outbound["plugin_opts"] = ss.PluginOptions ?? string.Empty;
                }

                break;

            case Hysteria2Settings hysteria:
                if (hysteria.Ports is not null)
                {
                    // «443,20000-30000» → ["443:443", "20000:30000"]; с server_ports поле server_port не указывается.
                    outbound["server_ports"] = new JsonArray([.. hysteria.Ports.Split(',', StringSplitOptions.TrimEntries).Select(PortRange)]);
                    if (hysteria.HopIntervalSeconds is { } hop)
                    {
                        outbound["hop_interval"] = hop.ToString(CultureInfo.InvariantCulture) + "s";
                    }
                }
                else
                {
                    outbound["server_port"] = profile.Server.Port;
                }

                outbound["password"] = hysteria.Password.Value;
                if (hysteria.UpMbps is { } up)
                {
                    outbound["up_mbps"] = up;
                }

                if (hysteria.DownMbps is { } down)
                {
                    outbound["down_mbps"] = down;
                }

                if (hysteria.Obfs is { } obfs)
                {
                    outbound["obfs"] = new JsonObject { ["type"] = obfs, ["password"] = hysteria.ObfsPassword?.Value ?? string.Empty };
                }

                break;

            case TuicSettings tuic:
                outbound["server_port"] = profile.Server.Port;
                outbound["uuid"] = tuic.Uuid.Value;
                outbound["password"] = tuic.Password.Value;
                outbound["congestion_control"] = tuic.CongestionControl;
                outbound["udp_relay_mode"] = tuic.UdpRelayMode;
                if (tuic.ZeroRttHandshake)
                {
                    outbound["zero_rtt_handshake"] = true;
                }

                break;
        }

        if (Tls(profile) is { } tls)
        {
            outbound["tls"] = tls;
        }

        if (!profile.Protocol.HasOwnTransport && Transport(profile.Transport) is { } transport)
        {
            outbound["transport"] = transport;
        }

        return outbound;
    }

    private static string Type(ProtocolSettings protocol) => protocol switch
    {
        VlessSettings => "vless",
        VmessSettings => "vmess",
        TrojanSettings => "trojan",
        ShadowsocksSettings => "shadowsocks",
        Hysteria2Settings => "hysteria2",
        TuicSettings => "tuic",
        _ => throw new NotSupportedException(protocol.GetType().Name),
    };

    private static string PortRange(string part) => part.Contains('-', StringComparison.Ordinal) ? part.Replace('-', ':') : $"{part}:{part}";

    private static JsonObject? Tls(Profile profile)
    {
        // QUIC (Hysteria2, TUIC) — без uTLS: отпечаток ClientHello браузера к QUIC не относится.
        var quic = profile.Protocol.HasOwnTransport;
        switch (profile.Security)
        {
            case TlsSecurity tls:
                var node = new JsonObject { ["enabled"] = true };
                if (tls.Sni is not null)
                {
                    node["server_name"] = tls.Sni;
                }

                if (tls.AllowInsecure)
                {
                    node["insecure"] = true;
                }

                var alpn = tls.Alpn.Count > 0 ? tls.Alpn.ToArray() : profile.Protocol is TuicSettings ? ["h3"] : [];
                if (alpn.Length > 0)
                {
                    node["alpn"] = new JsonArray([.. alpn.Select(a => (JsonNode)JsonValue.Create(a))]);
                }

                if (!quic && tls.Fingerprint is not null)
                {
                    node["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = tls.Fingerprint };
                }

                return node;

            case RealitySecurity reality:
                var realityNode = new JsonObject
                {
                    ["enabled"] = true,
                    ["server_name"] = reality.Sni,
                    ["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = reality.Fingerprint },
                    ["reality"] = new JsonObject
                    {
                        ["enabled"] = true,
                        ["public_key"] = reality.PublicKey.Value,
                        ["short_id"] = reality.ShortId?.Value ?? string.Empty,
                    },
                };
                return realityNode;

            default:
                return null;
        }
    }

    private static JsonObject? Transport(TransportSettings transport)
    {
        switch (transport)
        {
            case WsTransport ws:
                // Early data Xray («/path?ed=2048») — в sing-box отдельными полями.
                var (path, earlyData) = SplitEarlyData(ws.Path);
                var node = new JsonObject { ["type"] = "ws", ["path"] = path };
                if (ws.Host is not null)
                {
                    node["headers"] = new JsonObject { ["Host"] = ws.Host };
                }

                if (earlyData is { } bytes)
                {
                    node["max_early_data"] = bytes;
                    node["early_data_header_name"] = "Sec-WebSocket-Protocol";
                }

                return node;

            case HttpUpgradeTransport upgrade:
                var upgradeNode = new JsonObject { ["type"] = "httpupgrade", ["path"] = upgrade.Path };
                if (upgrade.Host is not null)
                {
                    upgradeNode["host"] = upgrade.Host;
                }

                return upgradeNode;

            case GrpcTransport grpc:
                return new JsonObject { ["type"] = "grpc", ["service_name"] = grpc.ServiceName };

            default:
                return null;
        }
    }

    /// <summary>«/ws?ed=2048» → («/ws», 2048); остальные параметры пути сохраняются.</summary>
    private static (string Path, int? EarlyData) SplitEarlyData(string path)
    {
        var queryAt = path.IndexOf('?', StringComparison.Ordinal);
        if (queryAt < 0)
        {
            return (path, null);
        }

        int? earlyData = null;
        var kept = new List<string>();
        foreach (var part in path[(queryAt + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("ed=", StringComparison.Ordinal)
                && int.TryParse(part[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            {
                earlyData = bytes;
            }
            else
            {
                kept.Add(part);
            }
        }

        var basePath = path[..queryAt];
        return (kept.Count == 0 ? basePath : basePath + "?" + string.Join('&', kept), earlyData);
    }

    private static JsonObject WireGuardEndpoint(Profile profile, WireGuardSettings wireGuard)
    {
        var peer = new JsonObject
        {
            ["address"] = profile.Server.Host,
            ["port"] = profile.Server.Port,
            ["public_key"] = wireGuard.PeerPublicKey.Value,
            ["allowed_ips"] = new JsonArray("0.0.0.0/0", "::/0"),
        };
        if (wireGuard.PreSharedKey is { } psk)
        {
            peer["pre_shared_key"] = psk.Value;
        }

        if (wireGuard.Reserved.Count == 3)
        {
            peer["reserved"] = new JsonArray([.. wireGuard.Reserved.Select(b => (JsonNode)JsonValue.Create(b))]);
        }

        var endpoint = new JsonObject
        {
            ["type"] = "wireguard",
            ["tag"] = ProxyTag,
            ["address"] = new JsonArray([.. wireGuard.LocalAddresses.Select(a => (JsonNode)JsonValue.Create(a))]),
            ["private_key"] = wireGuard.PrivateKey.Value,
            ["peers"] = new JsonArray(peer),
            // Адрес сервера — системным резолвером, не DNS через ещё не поднятый туннель.
            ["domain_resolver"] = "local",
        };
        if (wireGuard.Mtu is { } mtu)
        {
            endpoint["mtu"] = mtu;
        }

        return endpoint;
    }
}

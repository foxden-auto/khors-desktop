using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Khors.Core.Dns;
using Khors.Core.Geo;
using Khors.Core.Profiles;
using Khors.Core.Routing;

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

    /// <summary>Маршрутизация (ROADMAP 3.5); у временных ядер теста задержки — <see cref="RoutingPlan.Default"/>.</summary>
    public RoutingPlan Routing { get; init; } = RoutingPlan.Default;

    /// <summary>Категории гео-баз для правил <see cref="Routing"/>: sing-box не читает <c>.dat</c>.</summary>
    public GeoRuleData Geo { get; init; } = GeoRuleData.Empty;
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
    /// IPv6 через сервер: DNS отдаёт и AAAA (сначала IPv4), IPv6-соединения идут по правилам. Иначе — только IPv4:
    /// AAAA пустые, а IPv6-соединения (адрес из кэша, DNS браузера, адрес в программе) отклоняются сразу —
    /// без этого sing-box принимал бы соединение и обрывал его, если у сервера нет IPv6, и сайт «висел» бы.
    /// </summary>
    public bool Ipv6 { get; init; }

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

        if (options.Geo.FindMissing(options.Routing) is { } missing)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.GeoCategoryMissing, missing);
        }

        AddRoute(config, options);

        if (options.Tun is { } tun)
        {
            AddTun(config, tun, options.RemoteDns);
            AddDirectDns(config, options.Routing);
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
    private const string DirectDnsTag = "direct-dns";

    /// <summary>
    /// Маршрут: распознавание протокола, локальные сети, правила плана по порядку и выход для остального
    /// (<see cref="RoutingPlan.Final"/>). Соединение с локальных входов приходит с именем — перед первым правилом
    /// по адресу имя разрешается удалённым DNS через сервер (<c>resolve</c>); в TUN адрес уже есть.
    /// Гео-категории — встроенные наборы правил из <see cref="GeoRuleData"/>.
    /// </summary>
    private static void AddRoute(JsonObject config, SingBoxConfigOptions options)
    {
        var plan = options.Routing;
        var rules = new JsonArray(
            new JsonObject { ["action"] = "sniff" },
            plan.BypassLan
                ? new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag }
                : new JsonObject { ["ip_cidr"] = new JsonArray("127.0.0.0/8", "::1/128"), ["outbound"] = DirectTag });

        var ruleSets = new JsonArray();
        var resolved = false;
        foreach (var rule in plan.Rules)
        {
            if (rule.Match is RuleMatch.IpCidr or RuleMatch.GeoIp && !resolved)
            {
                if (config["dns"]!["final"] is null)
                {
                    AddRemoteDns(config, options.RemoteDns);
                }

                rules.Add(new JsonObject
                {
                    ["inbound"] = new JsonArray("socks-in", "http-in"),
                    ["action"] = "resolve",
                    ["server"] = RemoteDnsTag,
                    ["strategy"] = "ipv4_only",
                });
                resolved = true;
            }

            if (Condition(rule, options.Geo, ruleSets) is not { } node)
            {
                continue;
            }

            if (rule.Action == RuleAction.Block)
            {
                node["action"] = "reject";
            }
            else
            {
                node["outbound"] = rule.Action == RuleAction.Direct ? DirectTag : ProxyTag;
            }

            rules.Add(node);
        }

        var route = new JsonObject { ["rules"] = rules };
        if (ruleSets.Count > 0)
        {
            route["rule_set"] = ruleSets;
        }

        if (plan.Rules.Any(r => r.Match == RuleMatch.Process))
        {
            route["find_process"] = true;
        }

        if (plan.Final == RuleAction.Block)
        {
            rules.Add(new JsonObject { ["action"] = "reject" });
        }

        route["final"] = plan.Final == RuleAction.Direct ? DirectTag : ProxyTag;

        // Адрес сервера-домена разрешается системным резолвером.
        route["default_domain_resolver"] = "local";
        config["route"] = route;
    }

    /// <summary>
    /// Условие правила; гео-категория добавляет свой набор правил в <paramref name="ruleSets"/> (один раз).
    /// Пустая категория (например, атрибут, которого нет ни у одного домена) — <c>null</c>: правило ни с чем не совпадёт.
    /// </summary>
    private static JsonObject? Condition(RoutingRule rule, GeoRuleData geo, JsonArray ruleSets)
    {
        switch (rule.Match)
        {
            case RuleMatch.Domain:
                return new JsonObject { ["domain"] = new JsonArray(rule.Value) };
            case RuleMatch.DomainSuffix:
                return new JsonObject { ["domain_suffix"] = new JsonArray(rule.Value) };
            case RuleMatch.DomainKeyword:
                return new JsonObject { ["domain_keyword"] = new JsonArray(rule.Value) };
            case RuleMatch.IpCidr:
                return new JsonObject { ["ip_cidr"] = new JsonArray(rule.Value) };
            case RuleMatch.Process:
                return new JsonObject
                {
                    [RoutingRuleValidator.IsProcessPath(rule.Value) ? "process_path" : "process_name"] = new JsonArray(rule.Value),
                };
        }

        var tag = (rule.Match == RuleMatch.GeoSite ? "geosite-" : "geoip-") + rule.Value;
        if (!ruleSets.Any(r => (string?)r!["tag"] == tag))
        {
            var headless = rule.Match == RuleMatch.GeoSite ? GeoSiteRule(geo, rule.Value) : GeoIpRule(geo, rule.Value);
            if (headless is null)
            {
                return null;
            }

            ruleSets.Add(new JsonObject { ["type"] = "inline", ["tag"] = tag, ["rules"] = new JsonArray(headless) });
        }

        return new JsonObject { ["rule_set"] = new JsonArray(tag) };
    }

    /// <summary>Домены категории; <c>имя@атрибут</c> — только с атрибутом, <c>имя@!атрибут</c> — без него (как в Xray).</summary>
    private static JsonObject? GeoSiteRule(GeoRuleData geo, string value)
    {
        var category = geo.Sites[RoutingRuleValidator.GeoSiteBaseCode(value)];
        IEnumerable<GeoDomain> domains = category.Domains;
        if (value.IndexOf('@', StringComparison.Ordinal) is var at and >= 0)
        {
            var attribute = value[(at + 1)..];
            domains = attribute.StartsWith('!')
                ? domains.Where(d => !d.Attributes.Contains(attribute[1..]))
                : domains.Where(d => d.Attributes.Contains(attribute));
        }

        var rule = new JsonObject();
        foreach (var (type, field) in new[]
        {
            (GeoDomainType.Full, "domain"),
            (GeoDomainType.Domain, "domain_suffix"),
            (GeoDomainType.Keyword, "domain_keyword"),
            (GeoDomainType.Regex, "domain_regex"),
        })
        {
            var values = domains.Where(d => d.Type == type).Select(d => d.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (values.Length > 0)
            {
                rule[field] = ToArray(values);
            }
        }

        return rule.Count > 0 ? rule : null;
    }

    /// <summary>Подсети кода; <c>!код</c> и обратные записи базы — через <c>invert</c>.</summary>
    private static JsonObject? GeoIpRule(GeoRuleData geo, string value)
    {
        var negate = value.StartsWith('!');
        var category = geo.Ips[negate ? value[1..] : value];
        if (category.Networks.Count == 0)
        {
            return null;
        }

        var rule = new JsonObject { ["ip_cidr"] = ToArray(category.Networks.Select(n => n.ToString())) };
        if (negate != category.ReverseMatch)
        {
            rule["invert"] = true;
        }

        return rule;
    }

    /// <summary>
    /// Прямой DNS (TUN): имена из правил «напрямую» разрешает DNS провайдера, полученный по DHCP физического
    /// адаптера, — так обходные сайты получают адреса ближайших к пользователю узлов. Правила по имени повторяют
    /// маршрут по порядку, чтобы более раннее правило «через сервер» оставляло имя удалённому DNS.
    /// </summary>
    private static void AddDirectDns(JsonObject config, RoutingPlan plan)
    {
        if (!plan.Rules.Any(r => r.Action == RuleAction.Direct && IsNameRule(r)))
        {
            return;
        }

        var route = config["route"]!.AsObject();
        var dnsRules = new JsonArray();
        foreach (var rule in route["rules"]!.AsArray().OfType<JsonObject>())
        {
            var outbound = (string?)rule["outbound"];
            var byName = rule.Any(p => p.Key is "domain" or "domain_suffix" or "domain_keyword")
                || (rule["rule_set"] is JsonArray sets && sets.Any(t => ((string?)t)!.StartsWith("geosite-", StringComparison.Ordinal)));
            if (!byName || outbound is null)
            {
                continue;
            }

            var dnsRule = rule.DeepClone().AsObject();
            dnsRule.Remove("outbound");
            dnsRule["server"] = outbound == DirectTag ? DirectDnsTag : RemoteDnsTag;
            dnsRules.Add(dnsRule);
        }

        var dns = config["dns"]!.AsObject();
        dns["servers"]!.AsArray().Add(new JsonObject { ["type"] = "dhcp", ["tag"] = DirectDnsTag });
        dns["rules"] = dnsRules;
    }

    private static bool IsNameRule(RoutingRule rule) =>
        rule.Match is RuleMatch.Domain or RuleMatch.DomainSuffix or RuleMatch.DomainKeyword or RuleMatch.GeoSite;

    private static JsonArray ToArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);

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

        // У адаптера есть IPv6, чтобы IPv6 не шёл мимо туннеля. Без IPv6 через сервер приложениям — только IPv4-адреса:
        // Windows предпочла бы IPv6, а у многих серверов нет выхода в IPv6 — соединения браузера к таким адресам умирали.
        // На запросы AAAA sing-box отвечает пустым ответом без ошибки.
        dns["strategy"] = tun.Ipv6 ? "prefer_ipv4" : "ipv4_only";
        if (dns["final"] is null)
        {
            AddRemoteDns(config, remoteDns);
        }

        var route = config["route"]!.AsObject();
        var rules = route["rules"]!.AsArray();
        rules.Insert(1, new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        if (!tun.Ipv6)
        {
            // После перехвата DNS и локальной сети: IPv6-соединение отклоняется сразу (TCP — сброс), и программа
            // переходит на IPv4, а не ждёт ответа сервера без IPv6. Касается и прямых правил.
            rules.Insert(3, new JsonObject { ["ip_version"] = 6, ["action"] = "reject" });
        }

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

        if (wireGuard is AmneziaWireGuardSettings awg)
        {
            var amnezia = new JsonObject();
            Add(amnezia, "jc", awg.Jc);
            Add(amnezia, "jmin", awg.Jmin);
            Add(amnezia, "jmax", awg.Jmax);
            Add(amnezia, "s1", awg.S1);
            Add(amnezia, "s2", awg.S2);
            Add(amnezia, "s3", awg.S3);
            Add(amnezia, "s4", awg.S4);
            Add(amnezia, "h1", awg.H1);
            Add(amnezia, "h2", awg.H2);
            Add(amnezia, "h3", awg.H3);
            Add(amnezia, "h4", awg.H4);
            Add(amnezia, "i1", awg.I1);
            Add(amnezia, "i2", awg.I2);
            Add(amnezia, "i3", awg.I3);
            Add(amnezia, "i4", awg.I4);
            Add(amnezia, "i5", awg.I5);
            Add(amnezia, "header_protection_key", awg.HeaderProtectionKey);
            Add(amnezia, "content_padding_addition", awg.ContentPaddingAddition);
            Add(amnezia, "rekey_after_time", awg.RekeyAfterTime);
            Add(amnezia, "rekey_timeout", awg.RekeyTimeout);
            Add(amnezia, "reject_after_time", awg.RejectAfterTime);
            Add(amnezia, "keepalive_timeout", awg.KeepaliveTimeout);
            Add(amnezia, "max_handshake_attempts", awg.MaxHandshakeAttempts);
            Add(amnezia, "random_trailers", awg.RandomTrailers);
            Add(amnezia, "disable_cookies", awg.DisableCookies);
            foreach (var option in awg.ExtraOptions)
            {
                amnezia[option.Key] = option.Value;
            }

            endpoint["amnezia"] = amnezia;
        }

        return endpoint;
    }

    private static void Add(JsonObject target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static void Add(JsonObject target, string key, int? value)
    {
        if (value is { } number)
        {
            target[key] = number;
        }
    }

    private static void Add(JsonObject target, string key, bool? value)
    {
        if (value is { } flag)
        {
            target[key] = flag;
        }
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Khors.Core.Profiles;

namespace Khors.Core.Generators.Xray;

/// <summary>
/// Генерация конфига Xray-core из профиля (docs/SPEC.md, 3.3: режим «Системный прокси»).
/// Чистая функция: JSON строится только из модели, без правки готового текста (CLAUDE.md, правило 3).
/// </summary>
/// <remarks>Формат конфига — по документации Xray-core (xtls.github.io).</remarks>
public static class XrayConfigGenerator
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string BlockTag = "block";
    public const string SocksInboundTag = "socks-in";
    public const string HttpInboundTag = "http-in";
    public const string ApiTag = "api";

    // Локальные и служебные сети — напрямую. CIDR вместо geoip:private: гео-базы не поставляются до ROADMAP 3.6.
    private static readonly string[] s_privateNetworks =
    [
        "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16",
        "::1/128", "fc00::/7", "fe80::/10",
    ];

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static CoreConfigResult Generate(Profile profile, XrayConfigOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        if (profile.Core == CorePreference.SingBox)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.WrongCore, "core");
        }

        if (ProfileValidator.Validate(profile).FirstOrDefault(i => i.Severity == ProfileIssueSeverity.Error) is { } issue)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.ProfileInvalid, issue.Field);
        }

        if (FindUnsupported(profile) is { } field)
        {
            return CoreConfigResult.Failure(CoreConfigErrorCode.UnsupportedFeature, field);
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = options.LogLevel },
            ["inbounds"] = BuildInbounds(options),
            ["outbounds"] = new JsonArray(
                BuildProxyOutbound(profile),
                new JsonObject { ["tag"] = DirectTag, ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = BlockTag, ["protocol"] = "blackhole" }),
            ["routing"] = BuildRouting(),
        };

        if (options.ApiPort is { } apiPort)
        {
            config["api"] = new JsonObject
            {
                ["tag"] = ApiTag,
                ["listen"] = $"{options.ListenAddress}:{apiPort}",
                ["services"] = new JsonArray("StatsService"),
            };
            config["stats"] = new JsonObject();
            config["policy"] = new JsonObject
            {
                ["system"] = new JsonObject
                {
                    ["statsOutboundUplink"] = true,
                    ["statsOutboundDownlink"] = true,
                },
            };
        }

        return CoreConfigResult.Success(config.ToJsonString(s_jsonOptions));
    }

    /// <summary>
    /// Возможность профиля, которую Xray-core 26 не принимает: поле профиля или <c>null</c>, если Xray его запустит
    /// (без учёта ошибок профиля и выбора ядра). Такие профили автовыбор отдаёт sing-box.
    /// </summary>
    public static string? FindUnsupported(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        // Hysteria2, TUIC и WireGuard запускаются через sing-box (docs/SPEC.md, 3.4).
        if (profile.Protocol.HasOwnTransport)
        {
            return "protocol";
        }

        switch (profile.Protocol)
        {
            // SIP003-плагинов в Xray нет.
            case ShadowsocksSettings { Plugin: not null }:
                return "protocol.plugin";

            // Xray поддерживает только VMess AEAD (alterId = 0).
            case VmessSettings { AlterId: > 0 }:
                return "protocol.alterId";
        }

        // allowInsecure удалён в Xray 26; замена — закреплённый сертификат (pcs).
        if (profile.Security is TlsSecurity { AllowInsecure: true, PinnedPeerCertSha256.Count: 0 })
        {
            return "security.allowInsecure";
        }

        // Xray отказывается запускать VLESS (без VLESS Encryption) и Trojan без TLS к публичному адресу.
        var plaintextRestricted = profile.Protocol is VlessSettings { Encryption: "none" } or TrojanSettings;
        if (plaintextRestricted && profile.Security is NoSecurity && !IsPrivateAddress(profile.Server.Host))
        {
            return "security";
        }

        return null;
    }

    /// <summary>Частный адрес: loopback, локальные сети, link-local, CGNAT, localhost. Приближение правила Xray.</summary>
    private static bool IsPrivateAddress(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
        }

        var b = ip.GetAddressBytes();
        return b[0] is 10 or 127
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127);
    }

    private static JsonArray BuildInbounds(XrayConfigOptions options)
    {
        JsonObject Sniffing() => new()
        {
            ["enabled"] = true,
            ["destOverride"] = new JsonArray("http", "tls", "quic"),
            ["routeOnly"] = true,
        };

        return
        [
            new JsonObject
            {
                ["tag"] = SocksInboundTag,
                ["listen"] = options.ListenAddress,
                ["port"] = options.SocksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true },
                ["sniffing"] = Sniffing(),
            },
            new JsonObject
            {
                ["tag"] = HttpInboundTag,
                ["listen"] = options.ListenAddress,
                ["port"] = options.HttpPort,
                ["protocol"] = "http",
                ["sniffing"] = Sniffing(),
            },
        ];
    }

    private static JsonObject BuildRouting() => new()
    {
        ["domainStrategy"] = "AsIs",
        ["rules"] = new JsonArray(
            new JsonObject
            {
                ["type"] = "field",
                ["ip"] = ToArray(s_privateNetworks),
                ["outboundTag"] = DirectTag,
            },
            new JsonObject
            {
                ["type"] = "field",
                ["domain"] = new JsonArray("full:localhost"),
                ["outboundTag"] = DirectTag,
            }),
    };

    private static JsonObject BuildProxyOutbound(Profile profile)
    {
        var outbound = new JsonObject
        {
            ["tag"] = ProxyTag,
            ["protocol"] = ProtocolName(profile.Protocol),
            ["settings"] = BuildProtocolSettings(profile),
            ["streamSettings"] = BuildStreamSettings(profile),
        };

        if (profile.Mux is { Enabled: true } mux)
        {
            var muxNode = new JsonObject { ["enabled"] = true };
            if (mux.Concurrency is { } concurrency)
            {
                muxNode["concurrency"] = concurrency;
            }

            outbound["mux"] = muxNode;
        }

        return outbound;
    }

    private static string ProtocolName(ProtocolSettings protocol) => protocol switch
    {
        VlessSettings => "vless",
        VmessSettings => "vmess",
        TrojanSettings => "trojan",
        ShadowsocksSettings => "shadowsocks",
        _ => throw new NotSupportedException(protocol.GetType().Name),
    };

    private static JsonObject BuildProtocolSettings(Profile profile)
    {
        var host = profile.Server.Host;
        var port = profile.Server.Port;

        switch (profile.Protocol)
        {
            case VlessSettings vless:
                var vlessUser = new JsonObject { ["id"] = vless.Id.Value, ["encryption"] = vless.Encryption };
                if (!string.IsNullOrEmpty(vless.Flow))
                {
                    vlessUser["flow"] = vless.Flow;
                }

                return Vnext(host, port, vlessUser);

            case VmessSettings vmess:
                return Vnext(host, port, new JsonObject
                {
                    ["id"] = vmess.Id.Value,
                    ["alterId"] = vmess.AlterId,
                    ["security"] = vmess.Cipher,
                });

            case TrojanSettings trojan:
                return Servers(new JsonObject { ["address"] = host, ["port"] = port, ["password"] = trojan.Password.Value });

            case ShadowsocksSettings ss:
                return Servers(new JsonObject
                {
                    ["address"] = host,
                    ["port"] = port,
                    ["method"] = ss.Method,
                    ["password"] = ss.Password.Value,
                });

            default:
                throw new NotSupportedException(profile.Protocol.GetType().Name);
        }

        static JsonObject Vnext(string address, int port, JsonObject user) => new()
        {
            ["vnext"] = new JsonArray(new JsonObject
            {
                ["address"] = address,
                ["port"] = port,
                ["users"] = new JsonArray(user),
            }),
        };

        static JsonObject Servers(JsonObject server) => new() { ["servers"] = new JsonArray(server) };
    }

    private static JsonObject BuildStreamSettings(Profile profile)
    {
        var stream = new JsonObject();

        switch (profile.Transport)
        {
            case TcpTransport tcp:
                stream["network"] = "raw";
                if (tcp.HeaderType == "http")
                {
                    var request = new JsonObject { ["path"] = new JsonArray(tcp.Path ?? "/") };
                    if (tcp.Host is not null)
                    {
                        request["headers"] = new JsonObject { ["Host"] = ToArray(SplitHosts(tcp.Host)) };
                    }

                    stream["rawSettings"] = new JsonObject
                    {
                        ["header"] = new JsonObject { ["type"] = "http", ["request"] = request },
                    };
                }

                break;

            case WsTransport ws:
                stream["network"] = "ws";
                stream["wsSettings"] = PathAndHost(ws.Path, ws.Host);
                break;

            case HttpUpgradeTransport upgrade:
                stream["network"] = "httpupgrade";
                stream["httpupgradeSettings"] = PathAndHost(upgrade.Path, upgrade.Host);
                break;

            case GrpcTransport grpc:
                stream["network"] = "grpc";
                var grpcSettings = new JsonObject
                {
                    ["serviceName"] = grpc.ServiceName,
                    ["multiMode"] = grpc.Mode == "multi",
                };
                if (grpc.Authority is not null)
                {
                    grpcSettings["authority"] = grpc.Authority;
                }

                stream["grpcSettings"] = grpcSettings;
                break;

            case XhttpTransport xhttp:
                stream["network"] = "xhttp";
                var xhttpSettings = PathAndHost(xhttp.Path, xhttp.Host);
                xhttpSettings["mode"] = xhttp.Mode;
                if (xhttp.Extra is not null)
                {
                    // Валидатор уже проверил, что extra — JSON-объект; вставляем его как объект, а не строку.
                    xhttpSettings["extra"] = JsonNode.Parse(xhttp.Extra);
                }

                stream["xhttpSettings"] = xhttpSettings;
                break;
        }

        switch (profile.Security)
        {
            case NoSecurity:
                stream["security"] = "none";
                break;

            case TlsSecurity tls:
                stream["security"] = "tls";
                var tlsSettings = new JsonObject();
                if (tls.Sni is not null)
                {
                    tlsSettings["serverName"] = tls.Sni;
                }

                if (tls.Alpn.Count > 0)
                {
                    tlsSettings["alpn"] = ToArray(tls.Alpn);
                }

                if (tls.Fingerprint is not null)
                {
                    tlsSettings["fingerprint"] = tls.Fingerprint;
                }

                // allowInsecure в Xray 26 нет: допустим только вместе с pcs, который его и заменяет.
                if (tls.PinnedPeerCertSha256.Count > 0)
                {
                    tlsSettings["pinnedPeerCertSha256"] = string.Join(',', tls.PinnedPeerCertSha256);
                }

                if (tls.VerifyPeerCertByName.Count > 0)
                {
                    tlsSettings["verifyPeerCertByName"] = string.Join(',', tls.VerifyPeerCertByName);
                }

                stream["tlsSettings"] = tlsSettings;
                break;

            case RealitySecurity reality:
                stream["security"] = "reality";
                var realitySettings = new JsonObject
                {
                    ["serverName"] = reality.Sni,
                    ["fingerprint"] = reality.Fingerprint,
                    ["publicKey"] = reality.PublicKey.Value,
                    ["shortId"] = reality.ShortId?.Value ?? string.Empty,
                };
                if (reality.SpiderX is not null)
                {
                    realitySettings["spiderX"] = reality.SpiderX;
                }

                if (reality.MlDsa65Verify is not null)
                {
                    realitySettings["mldsa65Verify"] = reality.MlDsa65Verify;
                }

                stream["realitySettings"] = realitySettings;
                break;
        }

        return stream;
    }

    private static JsonObject PathAndHost(string path, string? host)
    {
        var settings = new JsonObject { ["path"] = path };
        if (host is not null)
        {
            settings["host"] = host;
        }

        return settings;
    }

    private static string[] SplitHosts(string hosts) =>
        hosts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonArray ToArray(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v))]);
}

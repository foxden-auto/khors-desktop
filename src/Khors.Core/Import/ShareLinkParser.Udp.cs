using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Khors.Core.Profiles;

namespace Khors.Core.Import;

public static partial class ShareLinkParser
{
    /// <summary>
    /// <c>hysteria2://[пароль@]хост[:порт|:порты]/?sni=…&amp;obfs=salamander&amp;obfs-password=…&amp;insecure=1&amp;pinSHA256=…</c>
    /// (схема ссылок Hysteria 2). Порт может быть списком и диапазонами (<c>443,20000-30000</c>); по умолчанию 443.
    /// </summary>
    private static Profile ParseHysteria2(string body)
    {
        var (beforeFragment, name) = LinkUrl.SplitFragment(body);
        var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var query = new QueryParameters(queryAt >= 0 ? beforeFragment[(queryAt + 1)..] : null);
        var beforeQuery = queryAt >= 0 ? beforeFragment[..queryAt] : beforeFragment;
        var authority = beforeQuery.Split('/', 2)[0];

        var at = authority.LastIndexOf('@');
        var password = at >= 0 ? Uri.UnescapeDataString(authority[..at]) : string.Empty;
        var (host, portSpec) = SplitHostAndPortSpec(authority[(at + 1)..]);
        var ports = portSpec ?? query.TakeFirst("mport", "ports");
        var port = FirstPort(portSpec) ?? 443;

        var protocol = new Hysteria2Settings
        {
            Password = new Secret(password),
            Obfs = query.Take("obfs"),
            ObfsPassword = query.Take("obfs-password") is { } obfsPassword ? new Secret(obfsPassword) : null,
            Ports = ports is not null && ports != port.ToString(CultureInfo.InvariantCulture) ? ports : null,
            HopIntervalSeconds = Number(query.TakeFirst("hop_interval", "hop-interval", "hopInterval")),
            UpMbps = Number(query.TakeFirst("upmbps", "up")),
            DownMbps = Number(query.TakeFirst("downmbps", "down")),
        };

        var security = new TlsSecurity
        {
            Sni = query.TakeFirst("sni", "peer"),
            Alpn = SplitList(query.Take("alpn")),
            AllowInsecure = IsTrue(query.TakeFirst("insecure", "allowInsecure", "allow_insecure")),
            PinnedPeerCertSha256 = SplitList(query.TakeFirst("pinSHA256", "pinsha256")),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(name, host, port),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Security = security,
            UnknownParams = query.Remaining(),
        };
    }

    /// <summary><c>tuic://uuid:пароль@хост:порт?congestion_control=bbr&amp;udp_relay_mode=native&amp;alpn=h3&amp;sni=…</c></summary>
    private static Profile ParseTuic(string body)
    {
        var url = LinkUrl.Parse(body);
        var userinfo = url.Userinfo is null ? string.Empty : url.Userinfo;
        var colon = userinfo.IndexOf(':', StringComparison.Ordinal);
        var uuid = Uri.UnescapeDataString(colon >= 0 ? userinfo[..colon] : userinfo);
        var password = colon >= 0 ? Uri.UnescapeDataString(userinfo[(colon + 1)..]) : string.Empty;
        if (uuid.Length == 0 || password.Length == 0)
        {
            throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "userinfo");
        }

        var query = new QueryParameters(url.Query);
        var protocol = new TuicSettings
        {
            Uuid = new Secret(uuid),
            Password = new Secret(password),
            CongestionControl = query.TakeFirst("congestion_control", "congestion-control", "congestion") ?? "cubic",
            UdpRelayMode = query.TakeFirst("udp_relay_mode", "udp-relay-mode") ?? "native",
            ZeroRttHandshake = IsTrue(query.TakeFirst("reduce_rtt", "zero_rtt_handshake", "zero-rtt-handshake")),
        };

        var security = new TlsSecurity
        {
            Sni = query.TakeFirst("sni", "peer"),
            Alpn = SplitList(query.Take("alpn")),
            AllowInsecure = IsTrue(query.TakeFirst("allow_insecure", "allowInsecure", "insecure")),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(url.Fragment, url.Host, url.Port),
            Server = new ServerEndpoint(url.Host, url.Port),
            Protocol = protocol,
            Security = security,
            UnknownParams = query.Remaining(),
        };
    }

    /// <summary>
    /// <c>wireguard://приватный_ключ@хост:порт?publickey=…&amp;presharedkey=…&amp;address=10.0.0.2/32,fd00::2/128&amp;reserved=1,2,3&amp;mtu=1280</c>
    /// (формат v2rayN и др.); также <c>wg://</c>.
    /// </summary>
    private static Profile ParseWireGuard(string body)
    {
        var url = LinkUrl.Parse(body);
        var privateKey = RequireUserinfo(url);
        var query = new QueryParameters(url.Query);

        var publicKey = query.TakeFirst("publickey", "public_key", "peer_public_key", "pbk")
            ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "publickey");
        var preSharedKey = query.TakeFirst("presharedkey", "preshared_key", "pre_shared_key", "psk");
        var addresses = SplitList(query.TakeFirst("address", "ip", "local_address")).Select(WithPrefix).ToArray();
        var reserved = SplitList(query.Take("reserved"))
            .Select(b => int.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : -1)
            .ToArray();

        var protocol = new WireGuardSettings
        {
            PrivateKey = new Secret(privateKey),
            PeerPublicKey = new Secret(publicKey),
            PreSharedKey = preSharedKey is null ? null : new Secret(preSharedKey),
            LocalAddresses = addresses,
            Reserved = reserved,
            Mtu = Number(query.Take("mtu")),
        };

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(url.Fragment, url.Host, url.Port),
            Server = new ServerEndpoint(url.Host, url.Port),
            Protocol = protocol,
            UnknownParams = query.Remaining(),
        };
    }

    /// <summary><c>хост</c>, <c>хост:443</c>, <c>хост:443,20000-30000</c>, <c>[v6]:порты</c>; порты — строкой или <c>null</c>.</summary>
    private static (string Host, string? PortSpec) SplitHostAndPortSpec(string hostPort)
    {
        string host;
        string? ports = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new LinkFormatException(LinkParseErrorCode.Malformed, "host");
            }

            host = hostPort[1..close];
            if (close + 1 < hostPort.Length)
            {
                ports = hostPort[(close + 1)..].TrimStart(':');
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            host = colon >= 0 ? hostPort[..colon] : hostPort;
            ports = colon >= 0 ? hostPort[(colon + 1)..] : null;
        }

        if (host.Length == 0)
        {
            throw new LinkFormatException(LinkParseErrorCode.MissingHost, "host");
        }

        if (ports is not null && (ports.Length == 0 || ports.Any(c => !char.IsAsciiDigit(c) && c is not ',' and not '-') || FirstPort(ports) is null))
        {
            throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "port");
        }

        return (host, ports);
    }

    private static int? FirstPort(string? ports)
    {
        var first = ports?.Split(',', '-')[0];
        return int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;
    }

    /// <summary>Число из начала строки: «100», «100 Mbps» → 100.</summary>
    private static int? Number(string? value)
    {
        var digits = new string((value ?? string.Empty).Trim().TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    /// <summary>Адрес интерфейса без префикса — как один адрес: /32 или /128.</summary>
    private static string WithPrefix(string address) =>
        address.Contains('/', StringComparison.Ordinal) || !IPAddress.TryParse(address, out var ip)
            ? address
            : address + (ip.AddressFamily == AddressFamily.InterNetworkV6 ? "/128" : "/32");
}

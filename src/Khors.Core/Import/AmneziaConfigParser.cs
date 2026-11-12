using System.Globalization;
using Khors.Core.Profiles;
using Khors.Core.Text;

namespace Khors.Core.Import;

internal static class AmneziaConfigParser
{
    public static bool LooksLikeConfig(string text)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && !trimmed.StartsWith(';'))
            {
                return trimmed.Equals("[Interface]", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public static Profile ParseVpnLink(string body)
    {
        var (payload, name) = LinkUrl.SplitFragment(body);
        if (!Base64Text.TryDecodeUtf8(payload, out var config) || !LooksLikeConfig(config))
        {
            throw new LinkFormatException(LinkParseErrorCode.Malformed, "vpn");
        }

        var profile = Parse(config);
        return string.IsNullOrWhiteSpace(name) ? profile : profile with { Name = name };
    }

    public static Profile Parse(string text)
    {
        var sections = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                sections.TryAdd(section, []);
                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (section is null || equals <= 0)
            {
                throw new LinkFormatException(LinkParseErrorCode.Malformed, section ?? "section");
            }

            sections[section].Add(new KeyValuePair<string, string>(line[..equals].Trim(), line[(equals + 1)..].Trim()));
        }

        if (!sections.TryGetValue("Interface", out var iface) || !sections.TryGetValue("Peer", out var peer))
        {
            throw new LinkFormatException(LinkParseErrorCode.Malformed, "section");
        }

        var privateKey = Value(iface, "PrivateKey") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "PrivateKey");
        var publicKey = Value(peer, "PublicKey") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "PublicKey");
        var endpoint = Value(peer, "Endpoint") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "Endpoint");
        var (host, port) = Endpoint(endpoint);
        var addresses = Values(iface, "Address").SelectMany(Split).Select(WithPrefix).ToArray();
        var wireGuard = new WireGuardSettings
        {
            PrivateKey = new Secret(privateKey),
            PeerPublicKey = new Secret(publicKey),
            PreSharedKey = Value(peer, "PresharedKey") is { } psk ? new Secret(psk) : null,
            LocalAddresses = addresses,
            Mtu = int.TryParse(Value(iface, "MTU"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mtu) ? mtu : null,
        };
        var options = iface.Where(f => AmneziaWireGuardOptions.IsOption(f.Key));
        var protocol = AmneziaWireGuardOptions.Create(wireGuard, options);

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PrivateKey", "Address", "MTU" };
        var unknown = iface.Where(f => !known.Contains(f.Key) && !AmneziaWireGuardOptions.IsOption(f.Key))
            .Select(f => new UnknownParam("interface." + f.Key, f.Value));
        var peerKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PublicKey", "PresharedKey", "Endpoint", "AllowedIPs", "PersistentKeepalive" };
        unknown = unknown.Concat(peer.Where(f => !peerKnown.Contains(f.Key)).Select(f => new UnknownParam("peer." + f.Key, f.Value)));

        return new Profile
        {
            Id = Guid.Empty,
            Name = "AmneziaWG",
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            UnknownParams = new EquatableArray<UnknownParam>(unknown),
        };
    }

    private static string? Value(IEnumerable<KeyValuePair<string, string>> fields, string key) =>
        fields.LastOrDefault(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static IEnumerable<string> Values(IEnumerable<KeyValuePair<string, string>> fields, string key) =>
        fields.Where(f => f.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Select(f => f.Value);

    private static IEnumerable<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (string Host, int Port) Endpoint(string value)
    {
        string host;
        string port;
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= value.Length || value[close + 1] != ':')
            {
                throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "Endpoint");
            }

            host = value[1..close];
            port = value[(close + 2)..];
        }
        else
        {
            var colon = value.LastIndexOf(':');
            if (colon <= 0)
            {
                throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "Endpoint");
            }

            host = value[..colon];
            port = value[(colon + 1)..];
        }

        return int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number is >= 1 and <= 65535
            ? (host, number)
            : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "Endpoint");
    }

    private static string WithPrefix(string address) =>
        address.Contains('/', StringComparison.Ordinal) ? address : address + (address.Contains(':', StringComparison.Ordinal) ? "/128" : "/32");
}

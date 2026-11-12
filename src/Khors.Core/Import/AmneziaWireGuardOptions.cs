using System.Globalization;
using Khors.Core.Profiles;

namespace Khors.Core.Import;

internal static class AmneziaWireGuardOptions
{
    private static readonly HashSet<string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        "jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5",
        "header_protection_key", "content_padding_addition", "rekey_after_time", "rekey_timeout", "reject_after_time",
        "keepalive_timeout", "max_handshake_attempts", "random_trailers", "disable_cookies",
    };

    public static bool IsOption(string name) => s_names.Any(option => Normalize(option) == Normalize(name));

    public static bool HasOptions(string? query) => query?.Split('&').Any(part => IsOption(part.Split('=', 2)[0])) == true;

    public static AmneziaWireGuardSettings Create(WireGuardSettings wireGuard, IEnumerable<KeyValuePair<string, string>> options)
    {
        var values = options.ToDictionary(o => Normalize(o.Key), o => o.Value, StringComparer.Ordinal);
        return Create(wireGuard, new QueryParameters(values), values.Keys);
    }

    public static AmneziaWireGuardSettings Create(WireGuardSettings wireGuard, QueryParameters options) =>
        Create(wireGuard, options, s_names);

    private static AmneziaWireGuardSettings Create(WireGuardSettings wireGuard, QueryParameters options, IEnumerable<string> names)
    {
        string? Take(string name) => options.Take(name) ?? options.Take(name.Replace("_", string.Empty, StringComparison.Ordinal));
        int? Number(string name) => int.TryParse(Take(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        bool? Boolean(string name) => Take(name)?.ToLowerInvariant() switch
        {
            "true" or "1" => true,
            "false" or "0" => false,
            _ => null,
        };

        var known = names.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        return new AmneziaWireGuardSettings
        {
            PrivateKey = wireGuard.PrivateKey,
            PeerPublicKey = wireGuard.PeerPublicKey,
            PreSharedKey = wireGuard.PreSharedKey,
            LocalAddresses = wireGuard.LocalAddresses,
            Reserved = wireGuard.Reserved,
            Mtu = wireGuard.Mtu,
            Jc = Number("jc"),
            Jmin = Number("jmin"),
            Jmax = Number("jmax"),
            S1 = Number("s1"),
            S2 = Number("s2"),
            S3 = Number("s3"),
            S4 = Number("s4"),
            H1 = Take("h1"),
            H2 = Take("h2"),
            H3 = Take("h3"),
            H4 = Take("h4"),
            I1 = Take("i1"),
            I2 = Take("i2"),
            I3 = Take("i3"),
            I4 = Take("i4"),
            I5 = Take("i5"),
            HeaderProtectionKey = Take("header_protection_key"),
            ContentPaddingAddition = Take("content_padding_addition"),
            RekeyAfterTime = Take("rekey_after_time"),
            RekeyTimeout = Take("rekey_timeout"),
            RejectAfterTime = Take("reject_after_time"),
            KeepaliveTimeout = Take("keepalive_timeout"),
            MaxHandshakeAttempts = Take("max_handshake_attempts"),
            RandomTrailers = Boolean("random_trailers"),
            DisableCookies = Boolean("disable_cookies"),
            ExtraOptions = new EquatableArray<UnknownParam>(options.Remaining().Where(o => known.Contains(Normalize(o.Key)))),
        };
    }

    private static string Normalize(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}

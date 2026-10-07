using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Khors.Core.Geo;
using Khors.Core.Routing;

namespace Khors.Core.Tests.Geo;

/// <summary>Сборка geosite.dat и geoip.dat в формате protobuf (только то, что нужно тестам; данные вымышленные).</summary>
internal static class GeoFiles
{
    public static byte[] GeoSiteFile(params byte[][] sites) => [.. sites.SelectMany(s => Field(1, s))];

    public static byte[] Site(string code, params byte[][] domains) => [.. Field(1, Encoding.UTF8.GetBytes(code)), .. domains.SelectMany(d => Field(2, d))];

    public static byte[] Domain(int type, string value, params string[] attributes) =>
        [.. Varint(1, (ulong)type), .. Field(2, Encoding.UTF8.GetBytes(value)), .. attributes.SelectMany(a => Field(3, [.. Field(1, Encoding.UTF8.GetBytes(a)), .. Varint(2, 1)]))];

    public static byte[] GeoIpFile(params byte[][] entries) => [.. entries.SelectMany(e => Field(1, e))];

    public static byte[] Ip(string code, bool reverse, params byte[][] cidrs) =>
        [.. Field(1, Encoding.UTF8.GetBytes(code)), .. cidrs.SelectMany(c => Field(2, c)), .. (reverse ? Varint(3, 1) : [])];

    public static byte[] Cidr(byte[] ip, int prefix) => [.. Field(1, ip), .. Varint(2, (ulong)prefix)];

    /// <summary>
    /// Базы из описания в векторе: <c>{"sites": {"код": ["domain:ru", "full:a.ru@ads,cn", "keyword:x", "regexp:^a$"]},
    /// "ips": {"код": ["203.0.113.0/24"], "!код": [...]}}</c> (<c>!</c> — обратная запись базы). Коды — в верхнем регистре,
    /// как в базах v2fly: Xray ищет категорию именно так.
    /// </summary>
    public static (byte[] Site, byte[] Ip) FromVector(JsonNode? geo)
    {
        var sites = geo?["sites"]?.AsObject().Select(p => Site(p.Key.ToUpperInvariant(), [.. p.Value!.AsArray().Select(d => VectorDomain(d!.GetValue<string>()))])) ?? [];
        var ips = geo?["ips"]?.AsObject().Select(p => Ip(
            p.Key.TrimStart('!').ToUpperInvariant(),
            reverse: p.Key.StartsWith('!'),
            [.. p.Value!.AsArray().Select(c => IPNetwork.Parse(c!.GetValue<string>())).Select(n => Cidr(n.BaseAddress.GetAddressBytes(), n.PrefixLength))])) ?? [];
        return (GeoSiteFile([.. sites]), GeoIpFile([.. ips]));
    }

    /// <summary>Категории плана из баз — как их читает Khors.Engines.</summary>
    public static GeoRuleData RuleData(RoutingPlan plan, (byte[] Site, byte[] Ip) files) => new(
        plan.GeoSiteCodes.Select(c => GeoSiteDatabase.ReadCategory(files.Site, c)).OfType<GeoSiteCategory>().ToDictionary(c => c.Code),
        plan.GeoIpCodes.Select(c => GeoIpDatabase.ReadCategory(files.Ip, c)).OfType<GeoIpCategory>().ToDictionary(c => c.Code));

    private static byte[] VectorDomain(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        var type = text[..colon] switch
        {
            "keyword" => 0,
            "regexp" => 1,
            "domain" => 2,
            "full" => 3,
            var other => throw new ArgumentException($"Неизвестный вид домена {other}", nameof(text)),
        };
        var rest = text[(colon + 1)..];
        var at = rest.LastIndexOf('@');
        return at < 0 ? Domain(type, rest) : Domain(type, rest[..at], rest[(at + 1)..].Split(','));
    }

    private static byte[] Field(int number, byte[] payload) => [.. VarintBytes((ulong)(number << 3 | 2)), .. VarintBytes((ulong)payload.Length), .. payload];

    private static byte[] Varint(int number, ulong value) => [.. VarintBytes((ulong)(number << 3)), .. VarintBytes(value)];

    private static byte[] VarintBytes(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value == 0 ? b : (byte)(b | 0x80));
        }
        while (value != 0);

        return [.. bytes];
    }
}

using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Khors.Core.Routing;

public enum RoutingRuleError
{
    Empty,
    InvalidDomain,
    InvalidKeyword,
    InvalidAddress,
    InvalidGeoCode,
    InvalidProcess,
}

/// <param name="Value">Каноническая форма; <c>null</c> — значение не принято (<see cref="Error"/>).</param>
public sealed record RoutingRuleValue(string? Value, RoutingRuleError? Error);

/// <summary>
/// Проверка и каноническая форма значения правила: имена — в нижнем регистре и punycode, у суффикса — без ведущей
/// точки, подсеть — в каноническом виде, коды гео-баз — в нижнем регистре. Значение уходит в конфиги ядер как есть,
/// поэтому всё, что ядро прочло бы иначе (пробелы, префиксы <c>domain:</c>, <c>geosite:</c>), отвергается.
/// </summary>
public static partial class RoutingRuleValidator
{
    private const int MaxLength = 253;
    private const int MaxPathLength = 260;

    private static readonly IdnMapping s_idn = new() { AllowUnassigned = false, UseStd3AsciiRules = false };

    public static RoutingRuleValue Normalize(RuleMatch match, string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Length == 0)
        {
            return new RoutingRuleValue(null, RoutingRuleError.Empty);
        }

        return match switch
        {
            RuleMatch.Domain => Domain(text, suffix: false),
            RuleMatch.DomainSuffix => Domain(text, suffix: true),
            RuleMatch.DomainKeyword => Keyword(text),
            RuleMatch.IpCidr => Address(text),
            RuleMatch.GeoSite => GeoCode(text, GeoSiteCode()),
            RuleMatch.GeoIp => GeoCode(text, GeoIpCode()),
            RuleMatch.Process => Process(text),
            _ => throw new ArgumentOutOfRangeException(nameof(match), match, null),
        };
    }

    public static bool IsValid(RoutingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return Enum.IsDefined(rule.Action) && Enum.IsDefined(rule.Match) && Normalize(rule.Match, rule.Value).Value == rule.Value;
    }

    private static RoutingRuleValue Domain(string text, bool suffix)
    {
        if (suffix && text.StartsWith('.'))
        {
            text = text[1..];
        }

        text = text.TrimEnd('.');
        string ascii;
        try
        {
            ascii = s_idn.GetAscii(text).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return new RoutingRuleValue(null, RoutingRuleError.InvalidDomain);
        }

        // Суффикс может быть зоной верхнего уровня (ru), точное имя — тоже одним словом (localhost).
        return ascii.Length <= MaxLength && DomainName().IsMatch(ascii)
            ? new RoutingRuleValue(ascii, null)
            : new RoutingRuleValue(null, RoutingRuleError.InvalidDomain);
    }

    private static RoutingRuleValue Keyword(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Length <= MaxLength && KeywordText().IsMatch(lower)
            ? new RoutingRuleValue(lower, null)
            : new RoutingRuleValue(null, RoutingRuleError.InvalidKeyword);
    }

    private static RoutingRuleValue Address(string text)
    {
        var invalid = new RoutingRuleValue(null, RoutingRuleError.InvalidAddress);

        // Индекс зоны (fe80::1%12) ядра в правилах не понимают.
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (text.Contains('%', StringComparison.Ordinal) || !IPAddress.TryParse(slash < 0 ? text : text[..slash], out var address))
        {
            return invalid;
        }

        if (slash < 0)
        {
            return new RoutingRuleValue(new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32).ToString(), null);
        }

        // Подсеть с битами за маской (10.1.2.3/8) не принимаем: пользователь, скорее всего, ошибся маской.
        return IPNetwork.TryParse(text, out var network) && network.BaseAddress.Equals(address)
            ? new RoutingRuleValue(network.ToString(), null)
            : invalid;
    }

    private static RoutingRuleValue GeoCode(string text, Regex pattern)
    {
        var lower = text.ToLowerInvariant();
        return pattern.IsMatch(lower)
            ? new RoutingRuleValue(lower, null)
            : new RoutingRuleValue(null, RoutingRuleError.InvalidGeoCode);
    }

    private static RoutingRuleValue Process(string text) =>
        text.Length <= MaxPathLength && ProcessText().IsMatch(text) && !text.EndsWith('\\') && !text.EndsWith('/')
            ? new RoutingRuleValue(text, null)
            : new RoutingRuleValue(null, RoutingRuleError.InvalidProcess);

    /// <summary>Путь к файлу, а не имя: правило сравнивает полный путь процесса.</summary>
    public static bool IsProcessPath(string value) =>
        value.Contains('\\', StringComparison.Ordinal) || value.Contains('/', StringComparison.Ordinal);

    /// <summary>Код категории без атрибута: <c>google@cn</c> → <c>google</c>.</summary>
    public static string GeoSiteBaseCode(string value) =>
        value.IndexOf('@', StringComparison.Ordinal) is var at and >= 0 ? value[..at] : value;

    [GeneratedRegex(@"^(?!-)[a-z0-9_-]{1,63}(?<!-)(\.(?!-)[a-z0-9_-]{1,63}(?<!-))*$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainName();

    [GeneratedRegex(@"^[\p{L}\p{N}._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeywordText();

    // Коды v2fly: category-ads-all, geolocation-!cn; атрибут — google@cn, category-ru@!ads.
    [GeneratedRegex(@"^[a-z0-9][a-z0-9_.!-]{0,62}(@!?[a-z0-9][a-z0-9_.-]{0,62})?$", RegexOptions.CultureInvariant)]
    private static partial Regex GeoSiteCode();

    [GeneratedRegex(@"^!?[a-z0-9][a-z0-9_.-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeoIpCode();

    // Имя или путь без кавычек, масок и управляющих символов.
    [GeneratedRegex(@"^[^\x00-\x1f""*?<>|]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessText();
}

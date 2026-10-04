using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Khors.Core.Text;

namespace Khors.Core.Diagnostics;

public sealed partial class SecretMasker
{
    // Файловые расширения, которые не считаются доменом в «имя:порт» (например, main.go:123 в трассировке Go).
    private static readonly HashSet<string> s_fileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "go", "cs", "json", "yaml", "yml", "log", "txt", "exe", "dll", "so", "py", "js",
    };

    /// <summary>
    /// Маскирует секреты в произвольном тексте: строках логов ядер, ссылках, URL подписок.
    /// </summary>
    /// <remarks>
    /// Обрабатываются: ссылки поддерживаемых схем (userinfo, адрес, чувствительные параметры,
    /// для http(s) — токены в пути и все значения запроса), пары «ключ=значение» / «ключ: значение»
    /// для известных ключей, UUID, адреса в форме «хост:порт», IPv4 и IPv6.
    /// Домены без порта вне ссылок и известных ключей не маскируются.
    /// </remarks>
    public string MaskText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        text = LinkPattern().Replace(text, m => MaskLink(m.Groups["scheme"].Value, m.Groups["rest"].Value));
        text = KeyValuePattern().Replace(text, MaskKeyValue);
        text = UuidInTextPattern().Replace(text, m => Mask(SecretKind.Uuid, m.Value));
        text = HostPortInTextPattern().Replace(text, MaskHostPortInText);
        text = IPv4InTextPattern().Replace(text, MaskIPv4InText);
        text = IPv6InTextPattern().Replace(text, MaskIPv6InText);
        return text;
    }

    private string MaskLink(string scheme, string rest)
    {
        // Знаки препинания в конце не часть ссылки: "... (vless://...)."
        var trimmed = rest.TrimEnd('.', ',', ';', ':', '!', '?', ')');
        var trailing = rest[trimmed.Length..];
        var lower = scheme.ToLowerInvariant();

        var fragmentAt = trimmed.IndexOf('#', StringComparison.Ordinal);
        var fragment = fragmentAt >= 0 ? trimmed[fragmentAt..] : string.Empty;
        var beforeFragment = fragmentAt >= 0 ? trimmed[..fragmentAt] : trimmed;

        var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var query = queryAt >= 0 ? beforeFragment[(queryAt + 1)..] : null;
        var beforeQuery = queryAt >= 0 ? beforeFragment[..queryAt] : beforeFragment;

        var isHttp = lower is "http" or "https" or "ws" or "wss";
        var maskedQuery = query is null ? string.Empty : "?" + MaskQuery(query, isHttp);

        // vmess://base64(JSON) и ss://base64(method:password@host:port) — без '@' в начале.
        if (!beforeQuery.Contains('@', StringComparison.Ordinal))
        {
            if (lower == "vmess")
            {
                return scheme + "://" + MaskVmessPayload(beforeQuery) + maskedQuery + fragment + trailing;
            }

            if (lower == "ss" && Base64Text.TryDecodeUtf8(beforeQuery, out var legacy) && legacy.Contains('@', StringComparison.Ordinal))
            {
                return MaskLink(scheme, legacy + maskedQuery + fragment) + trailing;
            }
        }

        var pathAt = beforeQuery.IndexOf('/', StringComparison.Ordinal);
        var authority = pathAt >= 0 ? beforeQuery[..pathAt] : beforeQuery;
        var path = pathAt >= 0 ? beforeQuery[pathAt..] : string.Empty;

        var userinfoAt = authority.LastIndexOf('@');
        var userinfo = userinfoAt >= 0 ? authority[..userinfoAt] : null;
        var hostPort = userinfoAt >= 0 ? authority[(userinfoAt + 1)..] : authority;

        var result = new StringBuilder(scheme).Append("://");
        if (userinfo is not null)
        {
            result.Append(lower == "ss" ? MaskShadowsocksUserinfo(userinfo) : MaskUserinfo(userinfo)).Append('@');
        }

        result.Append(MaskAuthorityHost(hostPort));
        result.Append(isHttp ? MaskHttpPath(path) : path);
        result.Append(maskedQuery).Append(fragment).Append(trailing);
        return result.ToString();
    }

    private string MaskUserinfo(string userinfo)
    {
        var colon = userinfo.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? MaskCredential(userinfo)
            : MaskCredential(userinfo[..colon]) + ":" + MaskCredential(userinfo[(colon + 1)..]);
    }

    /// <summary>SIP002: userinfo — base64(method:password) или method:password; метод шифрования оставляем.</summary>
    private string MaskShadowsocksUserinfo(string userinfo)
    {
        if (IsMask(userinfo))
        {
            return userinfo;
        }

        var plain = userinfo.Contains(':', StringComparison.Ordinal)
            ? userinfo
            : Base64Text.TryDecodeUtf8(userinfo, out var decoded) && decoded.Contains(':', StringComparison.Ordinal) ? decoded : null;

        if (plain is null)
        {
            return Mask(SecretKind.Password, userinfo);
        }

        var colon = plain.IndexOf(':', StringComparison.Ordinal);
        return plain[..colon] + ":" + Mask(SecretKind.Password, plain[(colon + 1)..]);
    }

    private string MaskAuthorityHost(string hostPort)
    {
        var match = HostWithPort().Match(hostPort);
        return match.Success
            ? MaskHost(match.Groups["host"].Value) + ":" + match.Groups["port"].Value
            : hostPort.Length == 0 ? hostPort : MaskHost(hostPort);
    }

    /// <summary>Сегменты пути, похожие на токен (≥ 12 символов, есть цифра), — маскируются.</summary>
    private string MaskHttpPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        var segments = path.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length >= 12 && segment.Any(char.IsAsciiDigit) && !IsMask(segment))
            {
                segments[i] = CreateMask(SecretKind.Token, segment);
            }
        }

        return string.Join('/', segments);
    }

    private string MaskQuery(string query, bool isHttp)
    {
        var parameters = query.Split('&');
        for (var i = 0; i < parameters.Length; i++)
        {
            var eq = parameters[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = parameters[i][..eq];
            var value = parameters[i][(eq + 1)..];
            if (value.Length == 0)
            {
                continue;
            }

            var kind = KindForKey(key, json: false);
            var masked = kind switch
            {
                SecretKind.Host => MaskHostValue(Uri.UnescapeDataString(value)),
                not null => Mask(kind.Value, value),
                null when isHttp => Mask(SecretKind.Token, value),
                null => value,
            };
            parameters[i] = key + "=" + masked;
        }

        return string.Join('&', parameters);
    }

    /// <summary>vmess://base64(JSON): JSON маскируется и кодируется обратно, чтобы ссылка осталась ссылкой.</summary>
    private string MaskVmessPayload(string payload)
    {
        if (IsMask(payload))
        {
            return payload;
        }

        if (Base64Text.TryDecodeUtf8(payload, out var json) && json.TrimStart().StartsWith('{'))
        {
            var masked = MaskJson(json, indented: false);
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(masked));
        }

        return Mask(SecretKind.Token, payload);
    }

    private string MaskKeyValue(Match match)
    {
        var kind = KindForKey(match.Groups["key"].Value, json: false);
        var value = match.Groups["val"].Value;
        var masked = kind switch
        {
            SecretKind.Host => MaskHostValue(value),
            not null => Mask(kind.Value, value),
            null => value,
        };

        return match.Value[..(match.Groups["val"].Index - match.Index)] + masked;
    }

    private string MaskHostPortInText(Match match)
    {
        var host = match.Groups["host"].Value;
        var lastDot = host.LastIndexOf('.');
        var tld = lastDot >= 0 ? host[(lastDot + 1)..] : string.Empty;
        return s_fileExtensions.Contains(tld) ? host : MaskHost(host);
    }

    private string MaskIPv4InText(Match match) =>
        IPAddress.TryParse(match.Value, out _) && match.Value.Split('.').All(o => int.Parse(o, CultureInfo.InvariantCulture) <= 255)
            ? MaskHost(match.Value)
            : match.Value;

    private string MaskIPv6InText(Match match)
    {
        var value = match.Value;
        var colons = value.Count(c => c == ':');
        var looksLikeAddress = value.Contains("::", StringComparison.Ordinal) || colons == 7;
        return looksLikeAddress && IsIPv6(value) ? MaskHost(value) : value;
    }

    [GeneratedRegex(@"(?<![\w+.-])(?<scheme>vless|vmess|trojan|ss|socks5?|https?|wss?|hysteria2?|hy2|tuic|wireguard|wg|anytls)://(?<rest>[^\s""'<>\\]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();

    [GeneratedRegex(
        @"(?<![\w-])(?<q>[""']?)(?<key>password|passwd|pass|pwd|uuid|pbk|public[_-]?key|private[_-]?key|peer[_-]?public[_-]?key|pre[_-]?shared[_-]?key|psk|sid|short[_-]?id|auth|auth[_-]?str|token|secret|obfs[_-]?password|sni|server[_-]?name|server|address|host|authority|vcn|verify[_-]?peer[_-]?cert[_-]?by[_-]?name)\k<q>\s*[:=]\s*[""']?(?<val>[^\s""'&,;{}\[\]]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"(?<![\w-])[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(?![\w-])")]
    private static partial Regex UuidInTextPattern();

    [GeneratedRegex(@"(?<![\w.{-])(?<host>(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]*[a-z0-9])(?=:\d{1,5}(?!\d))", RegexOptions.IgnoreCase)]
    private static partial Regex HostPortInTextPattern();

    [GeneratedRegex(@"(?<![\w.])\d{1,3}(?:\.\d{1,3}){3}(?!\w|\.\d)")]
    private static partial Regex IPv4InTextPattern();

    [GeneratedRegex(@"(?<![\w:.{-])[0-9a-fA-F:]*:[0-9a-fA-F:]*:[0-9a-fA-F:]*(?![\w:])")]
    private static partial Regex IPv6InTextPattern();
}

using Khors.Core.Profiles;

namespace Khors.Core.Import;

/// <summary>
/// Разбор ссылок <c>vless://</c>, <c>vmess://</c>, <c>trojan://</c>, <c>ss://</c> во внутреннюю модель.
/// Чистая функция: не обращается к сети и файлам.
/// </summary>
/// <remarks>
/// Профиль возвращается с <see cref="Profile.Id"/> = <see cref="Guid.Empty"/> и пустой датой обновления —
/// их назначает импорт. Нераспознанные параметры сохраняются в <see cref="Profile.UnknownParams"/>.
/// Параметры VLESS/Trojan/VMess — по стандарту ссылок Xray (XTLS/Xray-core, обсуждение #716).
/// </remarks>
public static partial class ShareLinkParser
{
    public static LinkParseResult Parse(string link)
    {
        ArgumentNullException.ThrowIfNull(link);

        var text = link.Trim();
        if (text.Length == 0)
        {
            return LinkParseResult.Failure(LinkParseErrorCode.Empty);
        }

        var separator = text.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            return LinkParseResult.Failure(LinkParseErrorCode.UnsupportedScheme, "scheme");
        }

        var scheme = text[..separator].ToLowerInvariant();
        var body = text[(separator + 3)..];
        try
        {
            return scheme switch
            {
                "vless" => LinkParseResult.Success(ParseVless(body)),
                "trojan" => LinkParseResult.Success(ParseTrojan(body)),
                "vmess" => LinkParseResult.Success(ParseVmess(body)),
                "ss" => LinkParseResult.Success(ParseShadowsocks(body)),
                _ => LinkParseResult.Failure(LinkParseErrorCode.UnsupportedScheme, "scheme"),
            };
        }
        catch (LinkFormatException ex)
        {
            return LinkParseResult.Failure(ex.Code, ex.Field);
        }
    }

    private static Profile ParseVless(string body)
    {
        var url = LinkUrl.Parse(body);
        var id = RequireUserinfo(url);
        var query = new QueryParameters(url.Query);

        var protocol = new VlessSettings
        {
            Id = new Secret(id),
            Flow = query.Take("flow"),
            Encryption = query.Take("encryption") ?? "none",
        };

        return BuildStandardProfile(url, protocol, query, defaultSecurity: "none");
    }

    private static Profile ParseTrojan(string body)
    {
        var url = LinkUrl.Parse(body);
        var password = RequireUserinfo(url);
        var query = new QueryParameters(url.Query);

        // У Trojan без параметра security подразумевается TLS.
        return BuildStandardProfile(url, new TrojanSettings { Password = new Secret(password) }, query, defaultSecurity: "tls");
    }

    /// <summary>Общая часть ссылок в стиле Xray: транспорт, безопасность, имя, неизвестные параметры.</summary>
    private static Profile BuildStandardProfile(LinkUrl url, ProtocolSettings protocol, QueryParameters query, string defaultSecurity)
    {
        var transport = ParseTransport(query);
        var security = ParseSecurity(query, defaultSecurity);

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(url.Fragment, url.Host, url.Port),
            Server = new ServerEndpoint(url.Host, url.Port),
            Protocol = protocol,
            Transport = transport,
            Security = security,
            UnknownParams = query.Remaining(),
        };
    }

    private static string RequireUserinfo(LinkUrl url)
    {
        var value = url.Userinfo is null ? string.Empty : Uri.UnescapeDataString(url.Userinfo);
        return value.Length > 0 ? value : throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "userinfo");
    }

    private static TransportSettings ParseTransport(QueryParameters query)
    {
        var type = (query.Take("type") ?? "tcp").ToLowerInvariant();
        return type switch
        {
            "tcp" or "raw" => ParseTcp(query),
            "ws" => new WsTransport { Path = query.Take("path") ?? "/", Host = query.Take("host") },
            "httpupgrade" => new HttpUpgradeTransport { Path = query.Take("path") ?? "/", Host = query.Take("host") },
            "grpc" => new GrpcTransport
            {
                ServiceName = query.Take("serviceName") ?? string.Empty,
                Mode = query.Take("mode") ?? "gun",
                Authority = query.Take("authority"),
            },
            "xhttp" or "splithttp" => new XhttpTransport
            {
                Path = query.Take("path") ?? "/",
                Host = query.Take("host"),
                Mode = query.Take("mode") ?? "auto",
                Extra = query.Take("extra"),
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedTransport, "type"),
        };
    }

    private static TcpTransport ParseTcp(QueryParameters query)
    {
        var headerType = query.Take("headerType") ?? "none";
        return headerType == "http"
            ? new TcpTransport { HeaderType = headerType, Host = query.Take("host"), Path = query.Take("path") }
            : new TcpTransport { HeaderType = headerType };
    }

    private static SecuritySettings ParseSecurity(QueryParameters query, string defaultSecurity)
    {
        var security = (query.Take("security") ?? defaultSecurity).ToLowerInvariant();
        return security switch
        {
            "none" => new NoSecurity(),
            "tls" => new TlsSecurity
            {
                Sni = query.TakeFirst("sni", "peer"),
                Alpn = SplitList(query.Take("alpn")),
                Fingerprint = query.Take("fp"),
                AllowInsecure = IsTrue(query.TakeFirst("allowInsecure", "insecure")),
                PinnedPeerCertSha256 = SplitList(query.Take("pcs")),
                VerifyPeerCertByName = SplitList(query.Take("vcn")),
            },
            "reality" => new RealitySecurity
            {
                Sni = query.TakeFirst("sni", "peer") ?? string.Empty,
                Fingerprint = query.Take("fp") ?? RealitySecurity.DefaultFingerprint,
                PublicKey = new Secret(query.Take("pbk") ?? string.Empty),
                ShortId = query.Take("sid") is { } sid ? new Secret(sid) : null,
                SpiderX = query.Take("spx"),
                MlDsa65Verify = query.Take("pqv"),
                SupportsX25519MlKem768 = query.Take("support-x25519mlkem768") is { } pq ? IsTrue(pq) : null,
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedSecurity, "security"),
        };
    }

    private static EquatableArray<string> SplitList(string? value) =>
        value is null
            ? default
            : new(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool IsTrue(string? value) => value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}

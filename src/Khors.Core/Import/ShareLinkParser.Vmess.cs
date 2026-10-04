using System.Globalization;
using System.Text.Json;
using Khors.Core.Profiles;
using Khors.Core.Text;

namespace Khors.Core.Import;

public static partial class ShareLinkParser
{
    /// <summary>
    /// <c>vmess://base64(JSON)</c> в формате v2rayN или <c>vmess://uuid@host:port?…</c> по стандарту Xray.
    /// </summary>
    private static Profile ParseVmess(string body)
    {
        var (beforeFragment, _) = LinkUrl.SplitFragment(body);
        var beforeQuery = beforeFragment.Split('?', 2)[0];
        return beforeQuery.Contains('@', StringComparison.Ordinal) ? ParseVmessStandard(body) : ParseVmessV2rayN(beforeQuery);
    }

    private static Profile ParseVmessStandard(string body)
    {
        var url = LinkUrl.Parse(body);
        var id = RequireUserinfo(url);
        var query = new QueryParameters(url.Query);

        var protocol = new VmessSettings { Id = new Secret(id), Cipher = query.Take("encryption") ?? "auto" };
        return BuildStandardProfile(url, protocol, query, defaultSecurity: "none");
    }

    private static Profile ParseVmessV2rayN(string payload)
    {
        if (!Base64Text.TryDecodeUtf8(payload, out var json))
        {
            throw new LinkFormatException(LinkParseErrorCode.InvalidBase64, "payload");
        }

        Dictionary<string, string> fields;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new LinkFormatException(LinkParseErrorCode.InvalidJson, "payload");
            }

            // Значения бывают и строками, и числами ("port": 443 / "443"); вложенные — как исходный JSON.
            fields = document.RootElement.EnumerateObject().ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString()!,
                    JsonValueKind.Null => string.Empty,
                    _ => p.Value.GetRawText(),
                },
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw new LinkFormatException(LinkParseErrorCode.InvalidJson, "payload");
        }

        var query = new QueryParameters(fields);
        query.Take("v");

        var host = query.Take("add") ?? throw new LinkFormatException(LinkParseErrorCode.MissingHost, "add");
        var port = LinkUrl.ParsePort(query.Take("port") ?? string.Empty);
        var id = query.Take("id") ?? throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "id");
        var alterIdText = query.Take("aid");
        var alterId = alterIdText is null ? 0
            : int.TryParse(alterIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aid) ? aid
            : throw new LinkFormatException(LinkParseErrorCode.Malformed, "aid");

        var protocol = new VmessSettings { Id = new Secret(id), AlterId = alterId, Cipher = query.Take("scy") ?? "auto" };
        var name = query.Take("ps");
        var transport = ParseVmessTransport(query);
        var security = ParseVmessSecurity(query);

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(name, host, port),
            Server = new ServerEndpoint(host, port),
            Protocol = protocol,
            Transport = transport,
            Security = security,
            UnknownParams = query.Remaining(),
        };
    }

    /// <summary>Поля v2rayN: net — транспорт, type — тип заголовка (tcp) или режим (grpc, xhttp), host, path.</summary>
    private static TransportSettings ParseVmessTransport(QueryParameters fields)
    {
        var net = (fields.Take("net") ?? "tcp").ToLowerInvariant();
        var type = fields.Take("type");
        var host = fields.Take("host");
        var path = fields.Take("path");

        return net switch
        {
            "tcp" or "raw" => type == "http"
                ? new TcpTransport { HeaderType = "http", Host = host, Path = path }
                : new TcpTransport { HeaderType = type ?? "none" },
            "ws" => new WsTransport { Path = path ?? "/", Host = host },
            "httpupgrade" => new HttpUpgradeTransport { Path = path ?? "/", Host = host },
            "grpc" => new GrpcTransport
            {
                ServiceName = path ?? string.Empty,
                Mode = type is "multi" ? "multi" : "gun",
                Authority = host,
            },
            "xhttp" or "splithttp" => new XhttpTransport
            {
                Path = path ?? "/",
                Host = host,
                Mode = type is null or "none" ? "auto" : type,
                Extra = fields.Take("extra"),
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedTransport, "net"),
        };
    }

    private static SecuritySettings ParseVmessSecurity(QueryParameters fields)
    {
        var tls = (fields.Take("tls") ?? "none").ToLowerInvariant();
        return tls switch
        {
            "none" => new NoSecurity(),
            "tls" => new TlsSecurity
            {
                Sni = fields.Take("sni"),
                Alpn = SplitList(fields.Take("alpn")),
                Fingerprint = fields.Take("fp"),
                AllowInsecure = IsTrue(fields.TakeFirst("allowInsecure", "insecure")),
            },
            _ => throw new LinkFormatException(LinkParseErrorCode.UnsupportedSecurity, "tls"),
        };
    }
}

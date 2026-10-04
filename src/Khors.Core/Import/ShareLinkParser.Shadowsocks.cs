using Khors.Core.Profiles;
using Khors.Core.Text;

namespace Khors.Core.Import;

public static partial class ShareLinkParser
{
    /// <summary>
    /// Shadowsocks: SIP002 (<c>ss://base64(method:password)@host:port/?plugin=…#name</c>),
    /// SIP002 с открытым userinfo для методов 2022 (<c>ss://method:password@…</c>, %-кодирование)
    /// и устаревшая форма <c>ss://base64(method:password@host:port)#name</c>.
    /// </summary>
    private static Profile ParseShadowsocks(string body)
    {
        var (beforeFragment, name) = LinkUrl.SplitFragment(body);
        var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var query = new QueryParameters(queryAt >= 0 ? beforeFragment[(queryAt + 1)..] : null);
        var beforeQuery = (queryAt >= 0 ? beforeFragment[..queryAt] : beforeFragment).TrimEnd('/');

        string credentials;
        string hostPort;
        var at = beforeQuery.LastIndexOf('@');
        if (at >= 0)
        {
            credentials = DecodeShadowsocksUserinfo(beforeQuery[..at]);
            hostPort = beforeQuery[(at + 1)..];
        }
        else
        {
            if (!Base64Text.TryDecodeUtf8(beforeQuery, out var legacy))
            {
                throw new LinkFormatException(LinkParseErrorCode.InvalidBase64, "userinfo");
            }

            var legacyAt = legacy.LastIndexOf('@');
            if (legacyAt < 0)
            {
                throw new LinkFormatException(LinkParseErrorCode.MissingHost, "host");
            }

            credentials = legacy[..legacyAt];
            hostPort = legacy[(legacyAt + 1)..];
        }

        var colon = credentials.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            throw new LinkFormatException(LinkParseErrorCode.MissingCredentials, "userinfo");
        }

        var (host, port) = LinkUrl.ParseHostPort(hostPort);
        var (plugin, pluginOptions) = SplitPlugin(query.Take("plugin"));

        return new Profile
        {
            Id = Guid.Empty,
            Name = LinkUrl.NameOrAddress(name, host, port),
            Server = new ServerEndpoint(host, port),
            Protocol = new ShadowsocksSettings
            {
                Method = credentials[..colon].ToLowerInvariant(),
                Password = new Secret(credentials[(colon + 1)..]),
                Plugin = plugin,
                PluginOptions = pluginOptions,
            },
            UnknownParams = query.Remaining(),
        };
    }

    private static string DecodeShadowsocksUserinfo(string userinfo)
    {
        var plain = Uri.UnescapeDataString(userinfo);
        if (plain.Contains(':', StringComparison.Ordinal))
        {
            return plain;
        }

        return Base64Text.TryDecodeUtf8(userinfo, out var decoded)
            ? decoded
            : throw new LinkFormatException(LinkParseErrorCode.InvalidBase64, "userinfo");
    }

    /// <summary>SIP003: <c>plugin=имя;опция=значение;…</c>.</summary>
    private static (string? Plugin, string? Options) SplitPlugin(string? value)
    {
        if (value is null)
        {
            return (null, null);
        }

        var semicolon = value.IndexOf(';', StringComparison.Ordinal);
        return semicolon < 0 ? (value, null) : (value[..semicolon], value[(semicolon + 1)..]);
    }
}

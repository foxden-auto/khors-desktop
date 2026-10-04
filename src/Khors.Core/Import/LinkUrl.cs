namespace Khors.Core.Import;

/// <summary>
/// Части ссылки вида <c>scheme://userinfo@host:port/path?query#fragment</c>.
/// Разбирается вручную: System.Uri меняет регистр и кодировку хоста и не принимает часть реальных ссылок.
/// </summary>
internal sealed record LinkUrl(string? Userinfo, string Host, int Port, string Path, string? Query, string? Fragment)
{
    /// <param name="body">Ссылка без <c>scheme://</c>.</param>
    public static LinkUrl Parse(string body)
    {
        var (beforeFragment, fragment) = SplitFragment(body);
        var queryAt = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        var query = queryAt >= 0 ? beforeFragment[(queryAt + 1)..] : null;
        var beforeQuery = queryAt >= 0 ? beforeFragment[..queryAt] : beforeFragment;

        var pathAt = beforeQuery.IndexOf('/', StringComparison.Ordinal);
        var authority = pathAt >= 0 ? beforeQuery[..pathAt] : beforeQuery;
        var path = pathAt >= 0 ? beforeQuery[pathAt..] : string.Empty;

        var userinfoAt = authority.LastIndexOf('@');
        var userinfo = userinfoAt >= 0 ? authority[..userinfoAt] : null;
        var (host, port) = ParseHostPort(authority[(userinfoAt + 1)..]);
        return new LinkUrl(userinfo, host, port, path, query, fragment);
    }

    /// <summary>Отделяет <c>#имя</c>; имя декодируется из %-кодирования.</summary>
    public static (string Before, string? Fragment) SplitFragment(string value)
    {
        var at = value.IndexOf('#', StringComparison.Ordinal);
        return at < 0 ? (value, null) : (value[..at], Uri.UnescapeDataString(value[(at + 1)..]));
    }

    /// <summary><c>host:port</c> или <c>[IPv6]:port</c>; хост IPv6 возвращается без скобок.</summary>
    public static (string Host, int Port) ParseHostPort(string hostPort)
    {
        string host;
        string portText;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= hostPort.Length || hostPort[close + 1] != ':')
            {
                throw new LinkFormatException(close < 0 ? LinkParseErrorCode.Malformed : LinkParseErrorCode.InvalidPort, "port");
            }

            host = hostPort[1..close];
            portText = hostPort[(close + 2)..];
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon < 0)
            {
                throw new LinkFormatException(hostPort.Length == 0 ? LinkParseErrorCode.MissingHost : LinkParseErrorCode.InvalidPort, "port");
            }

            host = hostPort[..colon];
            portText = hostPort[(colon + 1)..];
        }

        if (host.Length == 0)
        {
            throw new LinkFormatException(LinkParseErrorCode.MissingHost, "host");
        }

        return (host, ParsePort(portText));
    }

    public static int ParsePort(string text) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
        && port is >= 1 and <= 65535
            ? port
            : throw new LinkFormatException(LinkParseErrorCode.InvalidPort, "port");

    /// <summary>Имя профиля: из ссылки, иначе адрес сервера.</summary>
    public static string NameOrAddress(string? name, string host, int port)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        return host.Contains(':', StringComparison.Ordinal) ? $"[{host}]:{port}" : $"{host}:{port}";
    }
}

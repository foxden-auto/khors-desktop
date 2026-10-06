using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Khors.Core.Dns;

public enum DnsServerType
{
    /// <summary>Обычный DNS по UDP (порт 53).</summary>
    Udp,

    /// <summary>DNS по TCP (порт 53).</summary>
    Tcp,

    /// <summary>DNS over TLS (порт 853).</summary>
    Tls,

    /// <summary>DNS over HTTPS (порт 443, путь по умолчанию <c>/dns-query</c>).</summary>
    Https,
}

/// <summary>
/// Удалённый DNS-сервер (docs/SPEC.md, 4.5). Текстовая форма — <c>https://host[:port][/path]</c>, <c>tls://host[:port]</c>,
/// <c>udp://host[:port]</c>, <c>tcp://host[:port]</c> или просто IP (UDP). Строится только через <see cref="Parse"/>,
/// поэтому в конфиг ядра попадают лишь проверенные значения (CLAUDE.md, правило 8).
/// </summary>
public sealed record DnsServer
{
    private DnsServer(DnsServerType type, string host, int? port, string? path)
    {
        Type = type;
        Host = host;
        Port = port;
        Path = path;
    }

    public DnsServerType Type { get; }

    /// <summary>IP-адрес (IPv6 — без скобок) или имя хоста в ASCII.</summary>
    public string Host { get; }

    /// <summary>Порт, если он отличается от стандартного для типа; <c>null</c> — стандартный.</summary>
    public int? Port { get; }

    /// <summary>Путь DoH; <c>null</c> — <c>/dns-query</c>. Для остальных типов всегда <c>null</c>.</summary>
    public string? Path { get; }

    /// <summary>Хост — IP-адрес: серверу не нужно разрешение имени до подключения.</summary>
    public bool HostIsAddress => IPAddress.TryParse(Host, out _);

    /// <summary>
    /// Адрес в локальной сети или на этом компьютере (например, DNS роутера): запросы к нему идут напрямую,
    /// а не через сервер прокси, — сервер такой адрес не увидит.
    /// </summary>
    public bool IsLocalNetwork => IPAddress.TryParse(Host, out var address) && IsLocal(address);

    /// <summary>Разбор текстовой формы. Пробелы по краям допускаются.</summary>
    public static DnsServerParseResult Parse(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return DnsServerParseResult.Failure(DnsServerParseError.Empty);
        }

        // Пробелы, кавычки и управляющие символы внутри адреса Uri молча экранирует — такой текст не адрес.
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '<' or '>' or '\\' or '^' or '`' or '{' or '}' or '|'))
        {
            return DnsServerParseResult.Failure(DnsServerParseError.Malformed);
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            // Без схемы — только IP (UDP), например 1.1.1.1 или 2606:4700:4700::1111.
            // Сокращённые IPv4 вроде «1.1.1» IPAddress принимает, но это почти наверняка опечатка.
            var looksLikeAddress = value.Contains(':', StringComparison.Ordinal) || value.Count(c => c == '.') == 3;
            return looksLikeAddress && IPAddress.TryParse(value, out var bare)
                ? DnsServerParseResult.Success(new DnsServer(DnsServerType.Udp, bare.ToString(), null, null))
                : DnsServerParseResult.Failure(DnsServerParseError.Malformed);
        }

        var type = SchemeOf(value);
        if (type is null)
        {
            return DnsServerParseResult.Failure(DnsServerParseError.UnsupportedScheme);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return DnsServerParseResult.Failure(DnsServerParseError.Malformed);
        }

        string host;
        switch (uri.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                host = IPAddress.Parse(uri.Host.Trim('[', ']')).ToString();
                break;
            case UriHostNameType.Dns when uri.IdnHost.Contains('.', StringComparison.Ordinal):
                host = uri.IdnHost.ToLowerInvariant();
                break;
            default:
                return DnsServerParseResult.Failure(DnsServerParseError.InvalidHost);
        }

        // У своих схем (udp, tcp, tls) Uri не знает порта по умолчанию: без порта — -1.
        int? port = null;
        if (uri.Port is not -1 && !uri.IsDefaultPort)
        {
            if (uri.Port is < 1 or > 65535)
            {
                return DnsServerParseResult.Failure(DnsServerParseError.InvalidPort);
            }

            port = uri.Port == DefaultPort(type.Value) ? null : uri.Port;
        }

        string? path = null;
        var absolutePath = uri.AbsolutePath;
        if (type == DnsServerType.Https)
        {
            path = absolutePath is "/" or DefaultHttpsPath ? null : absolutePath;
        }
        else if (absolutePath != "/")
        {
            return DnsServerParseResult.Failure(DnsServerParseError.Malformed);
        }

        return DnsServerParseResult.Success(new DnsServer(type.Value, host, port, path));
    }

    public const string DefaultHttpsPath = "/dns-query";

    public static int DefaultPort(DnsServerType type) => type switch
    {
        DnsServerType.Tls => 853,
        DnsServerType.Https => 443,
        _ => 53,
    };

    /// <summary>Каноническая текстовая форма (её же принимает <see cref="Parse"/>).</summary>
    public override string ToString()
    {
        var scheme = Type switch
        {
            DnsServerType.Udp => "udp",
            DnsServerType.Tcp => "tcp",
            DnsServerType.Tls => "tls",
            _ => "https",
        };
        var host = Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]" : Host;
        var port = Port is { } p ? ":" + p.ToString(CultureInfo.InvariantCulture) : "";
        var path = Type == DnsServerType.Https ? Path ?? DefaultHttpsPath : "";
        return $"{scheme}://{host}{port}{path}";
    }

    private static DnsServerType? SchemeOf(string value)
    {
        var scheme = value[..value.IndexOf("://", StringComparison.Ordinal)].ToLowerInvariant();
        return scheme switch
        {
            "udp" => DnsServerType.Udp,
            "tcp" => DnsServerType.Tcp,
            "tls" => DnsServerType.Tls,
            "https" => DnsServerType.Https,
            _ => null,
        };
    }

    private static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
    }
}

/// <summary>Результат <see cref="DnsServer.Parse"/>: сервер или причина отказа (текст для пользователя — в ресурсах UI).</summary>
public sealed record DnsServerParseResult
{
    private DnsServerParseResult(DnsServer? server, DnsServerParseError? error)
    {
        Server = server;
        Error = error;
    }

    public DnsServer? Server { get; }

    public DnsServerParseError? Error { get; }

    [MemberNotNullWhen(true, nameof(Server))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Server is not null;

    internal static DnsServerParseResult Success(DnsServer server) => new(server, null);

    internal static DnsServerParseResult Failure(DnsServerParseError error) => new(null, error);
}

public enum DnsServerParseError
{
    Empty,

    /// <summary>Схема не из udp, tcp, tls, https (например, quic:// или sdns://).</summary>
    UnsupportedScheme,

    /// <summary>Не адрес: лишние части (логин, запрос, путь у не-DoH) или текст без схемы, который не IP.</summary>
    Malformed,

    /// <summary>Хост не IP и не имя вида <c>dns.example.com</c>.</summary>
    InvalidHost,

    InvalidPort,
}

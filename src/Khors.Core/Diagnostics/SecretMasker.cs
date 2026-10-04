using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Khors.Core.Diagnostics;

/// <summary>
/// Общий маскировщик секретов для логов, конфигов ядер и отчётов (CLAUDE.md, правило 5).
/// </summary>
/// <remarks>
/// Секрет заменяется маской вида <c>{uuid-1a2b3c}</c>: метка вида и первые 3 байта
/// HMAC-SHA256 значения на ключе экземпляра. Одинаковые значения в пределах одного
/// экземпляра дают одинаковую маску (можно сопоставлять строки отчёта), а по маске
/// нельзя восстановить или подобрать значение. Повторная маскировка ничего не меняет.
/// Loopback-адреса и <c>localhost</c> не маскируются. Класс потокобезопасен.
/// </remarks>
public sealed partial class SecretMasker
{
    private const int KeySize = 32;
    private const int TagBytes = 3;

    private readonly byte[] _key;

    /// <summary>Маскировщик со случайным ключом: маски не сопоставимы между экземплярами.</summary>
    public SecretMasker()
        : this(RandomNumberGenerator.GetBytes(KeySize))
    {
    }

    /// <summary>Маскировщик с заданным ключом HMAC (для воспроизводимых тестов).</summary>
    public SecretMasker(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("Key must not be empty.", nameof(key));
        }

        _key = key.ToArray();
    }

    /// <summary>Маскирует значение, вид которого известен вызывающему коду.</summary>
    public string Mask(SecretKind kind, string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length == 0 || IsMask(value))
        {
            return value;
        }

        return kind == SecretKind.Host ? MaskHost(value) : CreateMask(kind, value);
    }

    /// <summary>Проверяет, что строка целиком является маской.</summary>
    public static bool IsMask(string value) => MaskPattern().IsMatch(value);

    private string CreateMask(SecretKind kind, string value)
    {
        var label = Label(kind);
        var data = Encoding.UTF8.GetBytes(label + "\0" + value);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, data, mac);
        return "{" + label + "-" + Convert.ToHexStringLower(mac[..TagBytes]) + "}";
    }

    private static string Label(SecretKind kind) => kind switch
    {
        SecretKind.Uuid => "uuid",
        SecretKind.Password => "password",
        SecretKind.Key => "key",
        SecretKind.ShortId => "sid",
        SecretKind.Host => "host",
        SecretKind.Token => "token",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Маскирует адрес (домен или IP, IPv6 — с квадратными скобками или без), кроме loopback.</summary>
    private string MaskHost(string host)
    {
        if (IsMask(host))
        {
            return host;
        }

        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IsLoopback(bare) ? host : CreateMask(SecretKind.Host, bare.ToLowerInvariant());
    }

    /// <summary>
    /// Значение поля адреса: <c>host</c>, <c>host:port</c>, <c>[v6]:port</c> или URL.
    /// Строки, не похожие на адрес (например, тег DNS-сервера sing-box), маскируются как обычный текст.
    /// </summary>
    private string MaskHostValue(string value)
    {
        if (IsMask(value))
        {
            return value;
        }

        if (value.Contains("://", StringComparison.Ordinal))
        {
            return MaskText(value);
        }

        if (value.Contains(',', StringComparison.Ordinal))
        {
            return string.Join(',', value.Split(',').Select(MaskHostValue));
        }

        var hostPort = HostWithPort().Match(value);
        if (hostPort.Success && LooksLikeHost(hostPort.Groups["host"].Value))
        {
            return MaskHost(hostPort.Groups["host"].Value) + ":" + hostPort.Groups["port"].Value;
        }

        return LooksLikeHost(value) ? MaskHost(value) : MaskText(value);
    }

    private string MaskCredential(string value) =>
        Mask(UuidPattern().IsMatch(value) ? SecretKind.Uuid : SecretKind.Password, value);

    private static bool LooksLikeHost(string value)
    {
        var bare = value.StartsWith('[') && value.EndsWith(']') ? value[1..^1] : value;
        return IPAddress.TryParse(bare, out _) || DomainPattern().IsMatch(bare);
    }

    private static bool IsLoopback(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var ip)
            && (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any));
    }

    private static bool IsIPv6(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    /// <summary>
    /// Вид секрета по имени поля JSON, параметра ссылки или ключа в тексте.
    /// Имя нормализуется: нижний регистр, без <c>_</c> и <c>-</c>.
    /// </summary>
    private static SecretKind? KindForKey(string key, bool json)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "uuid" => SecretKind.Uuid,
            "id" when json => SecretKind.Uuid,
            "password" or "passwd" or "pass" or "pwd" or "auth" or "authstr" or "obfspassword" => SecretKind.Password,
            "user" or "username" when json => SecretKind.Password,
            "pbk" or "publickey" or "privatekey" or "peerpublickey" or "presharedkey" or "psk" => SecretKind.Key,
            "sid" or "shortid" or "shortids" => SecretKind.ShortId,
            "token" or "secret" or "accesstoken" => SecretKind.Token,
            "address" or "server" or "serveraddress" or "servername" or "sni" or "host" or "peer" or "endpoint" or "authority"
                or "vcn" or "verifypeercertbyname" => SecretKind.Host,
            "add" when json => SecretKind.Host, // адрес сервера в vmess:// (формат v2rayN)
            _ => null,
        };
    }

    [GeneratedRegex(@"^\{(?:uuid|password|key|sid|host|token)-[0-9a-f]{6}\}$")]
    private static partial Regex MaskPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex UuidPattern();

    [GeneratedRegex(@"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]*[a-z0-9]\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainPattern();

    [GeneratedRegex(@"^(?<host>\[[^\]]+\]|[^:\[\]]+):(?<port>\d{1,5})$")]
    private static partial Regex HostWithPort();
}

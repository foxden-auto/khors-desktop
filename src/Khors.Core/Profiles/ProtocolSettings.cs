using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>Протокол и его параметры. Hysteria2, TUIC, WireGuard добавляются в ROADMAP 2.3.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VlessSettings), "vless")]
[JsonDerivedType(typeof(VmessSettings), "vmess")]
[JsonDerivedType(typeof(TrojanSettings), "trojan")]
[JsonDerivedType(typeof(ShadowsocksSettings), "shadowsocks")]
public abstract record ProtocolSettings;

public sealed record VlessSettings : ProtocolSettings
{
    /// <summary>UUID пользователя или строка до 30 байт (Xray преобразует её в UUIDv5).</summary>
    public required Secret Id { get; init; }

    /// <summary>Например, <c>xtls-rprx-vision</c>; <c>null</c> — без flow.</summary>
    public string? Flow { get; init; }

    /// <summary><c>none</c> или параметры VLESS Encryption.</summary>
    public string Encryption { get; init => field = value ?? "none"; } = "none";
}

public sealed record VmessSettings : ProtocolSettings
{
    public required Secret Id { get; init; }

    public int AlterId { get; init; }

    /// <summary>Шифрование VMess: auto, aes-128-gcm, chacha20-poly1305, none, zero.</summary>
    public string Cipher { get; init => field = value ?? "auto"; } = "auto";
}

public sealed record TrojanSettings : ProtocolSettings
{
    public required Secret Password { get; init; }
}

public sealed record ShadowsocksSettings : ProtocolSettings
{
    /// <summary>Метод шифрования, например <c>2022-blake3-aes-128-gcm</c> или <c>chacha20-ietf-poly1305</c>.</summary>
    public required string Method { get; init; }

    public required Secret Password { get; init; }

    /// <summary>Плагин SIP003 (например, <c>obfs-local</c>) и его параметры.</summary>
    public string? Plugin { get; init; }

    public string? PluginOptions { get; init; }

    // Параметры плагина могут содержать адреса (obfs-host=...).
    public override string ToString() => $"ShadowsocksSettings {{ Method = {Method}, Password = {Password}, Plugin = {Plugin} }}";
}

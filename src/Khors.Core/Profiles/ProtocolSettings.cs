using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

/// <summary>
/// Протокол и его параметры. Hysteria2, TUIC и WireGuard работают поверх UDP со своим транспортом:
/// у таких профилей <see cref="Profile.Transport"/> не используется, TLS (для Hysteria2 и TUIC) — в <see cref="Profile.Security"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VlessSettings), "vless")]
[JsonDerivedType(typeof(VmessSettings), "vmess")]
[JsonDerivedType(typeof(TrojanSettings), "trojan")]
[JsonDerivedType(typeof(ShadowsocksSettings), "shadowsocks")]
[JsonDerivedType(typeof(Hysteria2Settings), "hysteria2")]
[JsonDerivedType(typeof(TuicSettings), "tuic")]
[JsonDerivedType(typeof(WireGuardSettings), "wireguard")]
public abstract record ProtocolSettings
{
    /// <summary>Протокол на UDP со своим транспортом (QUIC или WireGuard) — транспорт профиля не применяется.</summary>
    [JsonIgnore]
    public virtual bool HasOwnTransport => false;
}

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

/// <summary>Hysteria2 (QUIC). TLS-параметры — в <see cref="Profile.Security"/> (всегда <see cref="TlsSecurity"/>).</summary>
public sealed record Hysteria2Settings : ProtocolSettings
{
    /// <summary>Пароль (auth) — в том числе в виде «пользователь:пароль».</summary>
    public required Secret Password { get; init; }

    /// <summary>Обфускация: <c>salamander</c> или <c>null</c>.</summary>
    public string? Obfs { get; init; }

    public Secret? ObfsPassword { get; init; }

    /// <summary>Порты для смены порта (port hopping), например <c>443,20000-30000</c>; <c>null</c> — только порт сервера.</summary>
    public string? Ports { get; init; }

    /// <summary>Интервал смены порта, секунды.</summary>
    public int? HopIntervalSeconds { get; init; }

    /// <summary>Ограничения скорости, Мбит/с (Brutal); <c>null</c> — BBR.</summary>
    public int? UpMbps { get; init; }

    public int? DownMbps { get; init; }

    [JsonIgnore]
    public override bool HasOwnTransport => true;

    public override string ToString() => $"Hysteria2Settings {{ Obfs = {Obfs} }}";
}

/// <summary>TUIC v5 (QUIC). TLS-параметры — в <see cref="Profile.Security"/>.</summary>
public sealed record TuicSettings : ProtocolSettings
{
    public required Secret Uuid { get; init; }

    public required Secret Password { get; init; }

    /// <summary><c>cubic</c>, <c>new_reno</c> или <c>bbr</c>.</summary>
    public string CongestionControl { get; init => field = value ?? "cubic"; } = "cubic";

    /// <summary>Передача UDP: <c>native</c> или <c>quic</c>.</summary>
    public string UdpRelayMode { get; init => field = value ?? "native"; } = "native";

    /// <summary>0-RTT-рукопожатие (reduce_rtt).</summary>
    public bool ZeroRttHandshake { get; init; }

    [JsonIgnore]
    public override bool HasOwnTransport => true;

    public override string ToString() => $"TuicSettings {{ CongestionControl = {CongestionControl}, UdpRelayMode = {UdpRelayMode} }}";
}

/// <summary>WireGuard. Ключи — base64 по 32 байта.</summary>
public sealed record WireGuardSettings : ProtocolSettings
{
    public required Secret PrivateKey { get; init; }

    public required Secret PeerPublicKey { get; init; }

    public Secret? PreSharedKey { get; init; }

    /// <summary>Адреса интерфейса клиента с префиксом, например <c>10.0.0.2/32</c>, <c>fd00::2/128</c>.</summary>
    public EquatableArray<string> LocalAddresses { get; init; }

    /// <summary>Байты reserved (Cloudflare WARP и др.): ровно 3 числа 0…255 или пусто.</summary>
    public EquatableArray<int> Reserved { get; init; }

    public int? Mtu { get; init; }

    [JsonIgnore]
    public override bool HasOwnTransport => true;

    public override string ToString() => $"WireGuardSettings {{ Mtu = {Mtu} }}";
}

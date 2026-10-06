using System.Text.Json.Serialization;

namespace Khors.Core.Profiles;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(NoSecurity), "none")]
[JsonDerivedType(typeof(TlsSecurity), "tls")]
[JsonDerivedType(typeof(RealitySecurity), "reality")]
public abstract record SecuritySettings;

public sealed record NoSecurity : SecuritySettings;

public sealed record TlsSecurity : SecuritySettings
{
    public string? Sni { get; init; }

    public EquatableArray<string> Alpn { get; init; }

    /// <summary>Отпечаток uTLS (chrome, firefox, safari, …); <c>null</c> — по умолчанию ядра.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>
    /// Не проверять сертификат. Xray-core 26 этот режим удалил — вместо него
    /// <see cref="PinnedPeerCertSha256"/> и <see cref="VerifyPeerCertByName"/>.
    /// </summary>
    public bool AllowInsecure { get; init; }

    /// <summary>SHA-256 сертификата сервера в hex (двоеточия допустимы); параметр ссылки <c>pcs</c>.</summary>
    public EquatableArray<string> PinnedPeerCertSha256 { get; init; }

    /// <summary>Имена, по которым проверять сертификат вместо SNI; параметр ссылки <c>vcn</c>.</summary>
    public EquatableArray<string> VerifyPeerCertByName { get; init; }

    public override string ToString() => $"TlsSecurity {{ Fingerprint = {Fingerprint}, AllowInsecure = {AllowInsecure} }}";
}

public sealed record RealitySecurity : SecuritySettings
{
    /// <summary>Отпечаток по умолчанию для REALITY (docs/SPEC.md, 4.2).</summary>
    public const string DefaultFingerprint = "firefox";

    public required string Sni { get; init; }

    public string Fingerprint { get; init => field = value ?? DefaultFingerprint; } = DefaultFingerprint;

    /// <summary>Публичный ключ X25519 сервера, base64url (43 символа).</summary>
    public required Secret PublicKey { get; init; }

    /// <summary>Short id — до 16 шестнадцатеричных символов, чётной длины; пустой допустим.</summary>
    public Secret? ShortId { get; init; }

    public string? SpiderX { get; init; }

    /// <summary>Ключ проверки ML-DSA-65, base64url без паддинга, 1952 байта (параметр ссылки <c>pqv</c>, в Xray — <c>mldsa65Verify</c>).</summary>
    public string? MlDsa65Verify { get; init; }

    /// <summary>
    /// Сервер поддерживает (и может требовать) X25519MLKEM768 в ClientHello REALITY — параметр ссылки
    /// <c>support-x25519mlkem768</c>; <c>null</c> — не указано. Такой профиль запускается только через Xray:
    /// sing-box этот ключ не отправляет, и серверы Xray-core 26.9.8+ его не принимают (docs/SPEC.md, 3.4).
    /// </summary>
    public bool? SupportsX25519MlKem768 { get; init; }

    public override string ToString() => $"RealitySecurity {{ Fingerprint = {Fingerprint} }}";
}

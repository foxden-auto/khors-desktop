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

    public bool AllowInsecure { get; init; }

    public override string ToString() => $"TlsSecurity {{ Fingerprint = {Fingerprint}, AllowInsecure = {AllowInsecure} }}";
}

public sealed record RealitySecurity : SecuritySettings
{
    /// <summary>Отпечаток по умолчанию для REALITY (docs/SPEC.md, 4.2).</summary>
    public const string DefaultFingerprint = "chrome";

    public required string Sni { get; init; }

    public string Fingerprint { get; init => field = value ?? DefaultFingerprint; } = DefaultFingerprint;

    /// <summary>Публичный ключ X25519 сервера, base64url (43 символа).</summary>
    public required Secret PublicKey { get; init; }

    /// <summary>Short id — до 16 шестнадцатеричных символов, чётной длины; пустой допустим.</summary>
    public Secret? ShortId { get; init; }

    public string? SpiderX { get; init; }

    /// <summary>Ключ проверки ML-DSA-65 (параметр ссылки <c>pqv</c>, в Xray — <c>mldsa65Verify</c>).</summary>
    public string? MlDsa65Verify { get; init; }

    public override string ToString() => $"RealitySecurity {{ Fingerprint = {Fingerprint} }}";
}

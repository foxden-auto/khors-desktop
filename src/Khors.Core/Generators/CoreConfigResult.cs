using System.Diagnostics.CodeAnalysis;

namespace Khors.Core.Generators;

/// <summary>Результат генерации конфига ядра: JSON или причина, по которой его нельзя построить.</summary>
public sealed record CoreConfigResult
{
    private CoreConfigResult(string? json, CoreConfigError? error)
    {
        Json = json;
        Error = error;
    }

    /// <summary>Конфиг ядра. Содержит секреты профиля — в лог только через SecretMasker.MaskJson.</summary>
    public string? Json { get; }

    public CoreConfigError? Error { get; }

    [MemberNotNullWhen(true, nameof(Json))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Json is not null;

    public static CoreConfigResult Success(string json) => new(json ?? throw new ArgumentNullException(nameof(json)), null);

    public static CoreConfigResult Failure(CoreConfigErrorCode code, string? field = null) => new(null, new CoreConfigError(code, field));

    // Json не выводится: в нём секреты.
    public override string ToString() => IsSuccess ? "CoreConfigResult { Success }" : $"CoreConfigResult {{ Error = {Error} }}";
}

/// <param name="Field">Поле профиля, из-за которого конфиг не построен (например, <c>protocol.plugin</c>).</param>
public sealed record CoreConfigError(CoreConfigErrorCode Code, string? Field);

public enum CoreConfigErrorCode
{
    /// <summary>В профиле есть ошибки <see cref="Profiles.ProfileValidator"/>.</summary>
    ProfileInvalid,

    /// <summary>Профилю явно назначено другое ядро.</summary>
    WrongCore,

    /// <summary>Возможность профиля не поддерживается этим ядром.</summary>
    UnsupportedFeature,
}

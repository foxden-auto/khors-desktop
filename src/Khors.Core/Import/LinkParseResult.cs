using System.Diagnostics.CodeAnalysis;
using Khors.Core.Profiles;

namespace Khors.Core.Import;

/// <summary>Результат разбора ссылки: профиль или ошибка. Проверку профиля выполняет <see cref="ProfileValidator"/>.</summary>
public sealed record LinkParseResult
{
    private LinkParseResult(Profile? profile, LinkParseError? error)
    {
        Profile = profile;
        Error = error;
    }

    public Profile? Profile { get; }

    public LinkParseError? Error { get; }

    [MemberNotNullWhen(true, nameof(Profile))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Profile is not null;

    public static LinkParseResult Success(Profile profile) => new(profile ?? throw new ArgumentNullException(nameof(profile)), null);

    public static LinkParseResult Failure(LinkParseErrorCode code, string? field = null) => new(null, new LinkParseError(code, field));
}

/// <summary>Почему ссылку нельзя превратить в профиль. Текст для пользователя — в ресурсах UI по коду.</summary>
public sealed record LinkParseError(LinkParseErrorCode Code, string? Field);

public enum LinkParseErrorCode
{
    Empty,
    UnsupportedScheme,
    Malformed,
    MissingCredentials,
    MissingHost,
    InvalidPort,
    InvalidBase64,
    InvalidJson,
    UnsupportedTransport,
    UnsupportedSecurity,
}

/// <summary>Внутренний способ прервать разбор с кодом ошибки.</summary>
internal sealed class LinkFormatException(LinkParseErrorCode code, string? field = null) : Exception(code.ToString())
{
    public LinkParseErrorCode Code { get; } = code;

    public string? Field { get; } = field;
}

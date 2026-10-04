namespace Khors.Core.Profiles;

/// <summary>Проблема профиля. Текст для пользователя — в ресурсах UI по <see cref="Code"/>.</summary>
/// <param name="Field">Путь к полю модели, например <c>security.publicKey</c>.</param>
public sealed record ProfileIssue(ProfileIssueCode Code, ProfileIssueSeverity Severity, string Field);

public enum ProfileIssueSeverity
{
    /// <summary>С такой ошибкой подключение невозможно; профиль нельзя запускать.</summary>
    Error,

    /// <summary>Подключение возможно, но пользователю стоит знать.</summary>
    Warning,
}

public enum ProfileIssueCode
{
    NameEmpty,
    HostEmpty,
    HostInvalid,
    PortOutOfRange,
    IdEmpty,
    IdInvalid,
    AlterIdNegative,
    PasswordEmpty,
    MethodEmpty,
    FlowUnknown,
    FlowRequiresTcp,
    FlowRequiresTlsOrReality,
    MuxIncompatibleWithFlow,
    MuxConcurrencyOutOfRange,
    TransportModeUnknown,
    XhttpExtraInvalid,
    RealitySniEmpty,
    RealityPublicKeyInvalid,
    RealityShortIdInvalid,
    RealityTransportUnsupported,
    RealityMlDsa65VerifyInvalid,
    TlsPinnedCertInvalid,
    InsecureTls,
    UnknownParameters,
}

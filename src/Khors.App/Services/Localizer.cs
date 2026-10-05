using System.Globalization;
using Khors.App.Resources;
using Khors.Core.Generators;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Engines.Connection;

namespace Khors.App.Services;

/// <summary>Тексты для пользователя по кодам ошибок и проблем (ресурсы ru/en).</summary>
public static class Localizer
{
    public static string Get(string key) => TryGet(key) ?? key;

    public static string? TryGet(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);

    public static string Describe(LinkParseError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return Get($"LinkError_{error.Code}");
    }

    public static string Describe(ProfileIssueCode code) => Get($"Issue_{code}");

    public static string Describe(ConnectionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var exitCode = failure.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "?";
        return failure.Kind switch
        {
            ConnectionFailureKind.ProfileInvalid => Format("Failure_ProfileInvalid", failure.Issue is { } issue ? Describe(issue) : string.Empty),
            ConnectionFailureKind.UnsupportedByCore => Format("Failure_UnsupportedByCore", DescribeUnsupported(failure.ConfigError)),
            ConnectionFailureKind.CoreStartFailed or ConnectionFailureKind.CoreCrashed => Format($"Failure_{failure.Kind}", exitCode),
            _ => Get($"Failure_{failure.Kind}"),
        };
    }

    private static string DescribeUnsupported(CoreConfigError? error) => error switch
    {
        { Code: CoreConfigErrorCode.WrongCore } => Get("Unsupported_core"),
        { Field: { } field } => TryGet("Unsupported_" + field.Replace('.', '_')) ?? Get("Unsupported_Other"),
        _ => Get("Unsupported_Other"),
    };
}

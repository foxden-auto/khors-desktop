using System.Globalization;
using Khors.App.Resources;
using Khors.Core.Generators;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Subscriptions;
using Khors.Engines;
using Khors.Engines.Connection;
using Khors.Engines.Diagnostics;
using Khors.Engines.Latency;

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

    public static string Describe(SubscriptionUpdateError error) => Get($"SubscriptionError_{error}");

    /// <summary>Объём трафика: МБ до 1 ГБ, дальше ГБ.</summary>
    public static string Bytes(long bytes) => bytes >= 1L << 30
        ? Format("BytesGbFormat", bytes / (double)(1L << 30))
        : Format("BytesMbFormat", bytes / (double)(1L << 20));

    public static string Describe(LatencyResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result is { Status: LatencyStatus.Ok, Delay: { } delay }
            ? Format("Latency_Ok", (int)Math.Round(delay.TotalMilliseconds))
            : Get($"Latency_{result.Status}");
    }

    public static string Describe(ConnectionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var exitCode = failure.ExitCode?.ToString(CultureInfo.CurrentCulture) ?? "?";
        var core = CoreName(failure.Core);
        return failure.Kind switch
        {
            ConnectionFailureKind.ProfileInvalid => Format("Failure_ProfileInvalid", failure.Issue is { } issue ? Describe(issue) : string.Empty),
            ConnectionFailureKind.UnsupportedByCore => Format("Failure_UnsupportedByCore", core, DescribeUnsupported(failure.ConfigError)),
            ConnectionFailureKind.CoreStartFailed or ConnectionFailureKind.CoreCrashed => Format($"Failure_{failure.Kind}", core, exitCode),
            ConnectionFailureKind.CoreNotFound => Format("Failure_CoreNotFound", core),
            _ => Get($"Failure_{failure.Kind}"),
        };
    }

    /// <summary>Подсказка по причине из лога ядра. REALITY в sing-box — со своим советом (переключить на Xray).</summary>
    public static string Describe(CoreDiagnosis diagnosis)
    {
        ArgumentNullException.ThrowIfNull(diagnosis);
        return diagnosis is { Problem: CoreProblem.RealityRejected, Core: CoreKind.SingBox }
            ? Get("Problem_RealityRejected_SingBox")
            : Get($"Problem_{diagnosis.Problem}");
    }

    /// <summary>Название ядра — как у проекта (не переводится).</summary>
    public static string CoreName(CoreKind? core) => core switch
    {
        CoreKind.Xray => "Xray",
        CoreKind.SingBox => "sing-box",
        _ => string.Empty,
    };

    /// <summary>Что не поддерживает ядро — по полю профиля (<see cref="CoreConfigError.Field"/>).</summary>
    public static string DescribeUnsupported(string field) =>
        TryGet("Unsupported_" + field.Replace('.', '_')) ?? Get("Unsupported_Other");

    private static string DescribeUnsupported(CoreConfigError? error) => error switch
    {
        { Code: CoreConfigErrorCode.WrongCore } => Get("Unsupported_core"),
        { Field: { } field } => DescribeUnsupported(field),
        _ => Get("Unsupported_Other"),
    };
}

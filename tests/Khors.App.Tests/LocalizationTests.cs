using System.Collections;
using System.Globalization;
using System.Resources;
using Khors.App.Resources;
using Khors.App.Services;
using Khors.Core.Generators;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Xunit;

namespace Khors.App.Tests;

/// <summary>Каждый код ошибки показывается пользователю текстом на обоих языках, а не именем ключа.</summary>
public class LocalizationTests
{
    private static readonly CultureInfo s_russian = CultureInfo.GetCultureInfo("ru");

    // Поля, которые XrayConfigGenerator возвращает в CoreConfigError (UnsupportedFeature).
    private static readonly string[] s_unsupportedFields = ["protocol.plugin", "protocol.alterId", "security.allowInsecure", "security"];

    public static TheoryData<string> RequiredKeys
    {
        get
        {
            var keys = new TheoryData<string>();
            foreach (var code in Enum.GetNames<LinkParseErrorCode>())
            {
                keys.Add($"LinkError_{code}");
            }

            foreach (var code in Enum.GetNames<ProfileIssueCode>())
            {
                keys.Add($"Issue_{code}");
            }

            foreach (var kind in Enum.GetNames<ConnectionFailureKind>())
            {
                keys.Add($"Failure_{kind}");
            }

            foreach (var status in Enum.GetNames<Khors.Engines.Latency.LatencyStatus>())
            {
                keys.Add($"Latency_{status}");
            }

            foreach (var error in Enum.GetNames<Khors.Core.Subscriptions.SubscriptionUpdateError>())
            {
                keys.Add($"SubscriptionError_{error}");
            }

            foreach (var state in Enum.GetNames<ConnectionState>())
            {
                keys.Add($"Status{state}");
            }

            foreach (var unsupported in s_unsupportedFields)
            {
                keys.Add("Unsupported_" + unsupported.Replace('.', '_'));
            }

            return keys;
        }
    }

    [Theory]
    [MemberData(nameof(RequiredKeys))]
    public void KeyHasEnglishAndRussianText(string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(Neutral()[key]), $"Нет английского текста для {key}");
        Assert.False(string.IsNullOrWhiteSpace(Russian()[key]), $"Нет русского текста для {key}");
    }

    [Fact]
    public void BothLanguagesHaveTheSameKeys() =>
        Assert.Equal(Neutral().Keys.Order(), Russian().Keys.Order());

    [Fact]
    public void FailureDescriptionsAreTextNotKeys()
    {
        var previous = Strings.Culture;
        try
        {
            foreach (var culture in new[] { CultureInfo.InvariantCulture, s_russian })
            {
                Strings.Culture = culture;
                foreach (var kind in Enum.GetValues<ConnectionFailureKind>())
                {
                    var failure = new ConnectionFailure(
                        kind,
                        Issue: ProfileIssueCode.PortOutOfRange,
                        ConfigError: new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security.allowInsecure"),
                        ExitCode: 23);

                    var text = Localizer.Describe(failure);

                    Assert.DoesNotContain("Failure_", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("Unsupported_", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("Issue_", text, StringComparison.Ordinal);
                }
            }
        }
        finally
        {
            Strings.Culture = previous;
        }
    }

    private static Dictionary<string, string> Neutral() => Read(Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false));

    private static Dictionary<string, string> Russian() => Read(Strings.ResourceManager.GetResourceSet(s_russian, createIfNotExists: true, tryParents: false));

    private static Dictionary<string, string> Read(ResourceSet? set)
    {
        Assert.NotNull(set);
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => e.Value as string ?? string.Empty);
    }
}

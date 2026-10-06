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

    // Поля, которые генераторы Xray и sing-box возвращают в CoreConfigError (UnsupportedFeature).
    private static readonly string[] s_unsupportedFields =
    [
        "protocol.plugin", "protocol.alterId", "security.allowInsecure", "security", "protocol",
        "protocol.encryption", "transport", "transport.headerType", "transport.mode",
        "security.pinnedPeerCertSha256", "security.verifyPeerCertByName", "security.supportsX25519MlKem768", "security.mlDsa65Verify",
    ];

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

            foreach (var problem in Enum.GetNames<Khors.Engines.Diagnostics.CoreProblem>())
            {
                keys.Add($"Problem_{problem}");
            }

            foreach (var state in Enum.GetNames<Khors.Platform.ServiceState>())
            {
                keys.Add($"ServiceState_{state}");
            }

            foreach (var action in Enum.GetNames<Khors.Platform.ServiceSetupAction>())
            {
                keys.Add($"ServiceSetup_{action}_Succeeded");
                keys.Add($"ServiceSetup_{action}_Failed");
            }

            foreach (var error in Enum.GetNames<Khors.Core.Dns.DnsServerParseError>().Where(e => e != "Empty"))
            {
                keys.Add($"DnsError_{error}");
            }

            foreach (var type in Enum.GetNames<Khors.Core.Dns.DnsServerType>())
            {
                keys.Add($"DnsKind_{type}");
            }

            keys.Add("ServiceSetup_Cancelled");
            keys.Add("ServiceSetup_SetupNotFound");
            keys.Add("Problem_RealityRejected_SingBox");
            keys.Add("ConnectionProblemFormat");

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
                    foreach (int? exitCode in new int?[] { 23, null })
                    {
                        var failure = new ConnectionFailure(
                            kind,
                            Issue: ProfileIssueCode.PortOutOfRange,
                            ConfigError: new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security.allowInsecure"),
                            ExitCode: exitCode);

                        var text = Localizer.Describe(failure);

                        Assert.DoesNotContain("Failure_", text, StringComparison.Ordinal);
                        Assert.DoesNotContain("Unsupported_", text, StringComparison.Ordinal);
                        Assert.DoesNotContain("Issue_", text, StringComparison.Ordinal);
                        Assert.DoesNotContain("?", text, StringComparison.Ordinal);
                    }
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

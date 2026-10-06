using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Generators.SingBox;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Xunit;

namespace Khors.Core.Tests.Generators;

/// <summary>
/// Снапшоты конфигов sing-box: <c>Vectors/singbox-config/*.json</c> — ссылка (+ <c>core</c>), параметры (порты, уровень лога,
/// <c>tun</c> с <c>excludeAddresses</c>, <c>upstreamSocksPort</c> для цепочки через Xray) и ожидаемый конфиг или ошибка. Каждый ожидаемый конфиг проверяется настоящим sing-box (<c>sing-box check</c>), если он скачан.
/// </summary>
public class SingBoxConfigVectorTests
{
    private static readonly string s_directory = Path.Combine(AppContext.BaseDirectory, "Vectors", "singbox-config");

    public static TheoryData<string> AllVectors => new(VectorNames(_ => true));

    public static TheoryData<string> SuccessVectors => new(VectorNames(v => v["expected"] is not null));

    [Theory]
    [MemberData(nameof(AllVectors))]
    public void VectorGeneratesExpectedConfig(string name)
    {
        var vector = Load(name);
        var result = Generate(vector);

        if (vector["expected"] is { } expected)
        {
            Assert.True(result.IsSuccess, $"Ожидался конфиг, получена ошибка {result.Error}");
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(result.Json)), $"Конфиг отличается от ожидаемого:\n{result.Json}");
        }
        else
        {
            var error = vector["error"]!;
            Assert.False(result.IsSuccess, "Ожидалась ошибка генерации");
            Assert.Equal(Enum.Parse<CoreConfigErrorCode>(error["code"]!.GetValue<string>()), result.Error.Code);
            Assert.Equal(error["field"]?.GetValue<string>(), result.Error.Field);
        }
    }

    [Theory]
    [MemberData(nameof(SuccessVectors))]
    public void MaskedConfigContainsNoSecrets(string name)
    {
        var vector = Load(name);
        var masked = new SecretMasker(Encoding.UTF8.GetBytes("singbox-test")).MaskJson(Generate(vector).Json!);

        Assert.All(ProfileSecrets.Of(BuildProfile(vector)), s => Assert.DoesNotContain(s, masked, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Конфиг принимает настоящий sing-box той версии, что закреплена в cores.lock.json.</summary>
    [Theory]
    [MemberData(nameof(SuccessVectors))]
    public async Task RealSingBoxAcceptsConfig(string name)
    {
        var singBox = FindSingBox();
        Assert.SkipWhen(singBox is null, "sing-box не скачан: dotnet run tools/cores/fetch-cores.cs");

        var startInfo = new ProcessStartInfo(singBox!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "check", "-c", "stdin", "--disable-color" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        await process.StandardInput.WriteAsync(Generate(Load(name)).Json);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        Assert.True(process.ExitCode == 0, $"sing-box check: код {process.ExitCode}\n{await stdout}{await stderr}");
    }

    private static CoreConfigResult Generate(JsonObject vector)
    {
        var options = vector["options"]!;
        return SingBoxConfigGenerator.Generate(BuildProfile(vector), new SingBoxConfigOptions
        {
            SocksPort = options["socksPort"]!.GetValue<int>(),
            HttpPort = options["httpPort"]!.GetValue<int>(),
            LogLevel = options["logLevel"]?.GetValue<string>() ?? "warning",
            UpstreamSocksPort = options["upstreamSocksPort"]?.GetValue<int>(),
            ClashApi = options["clashApi"] is { } clashApi
                ? new ClashApiOptions(clashApi["port"]!.GetValue<int>(), clashApi["secret"]!.GetValue<string>())
                : null,
            Tun = options["tun"] is { } tun
                ? new SingBoxTunOptions { ExcludeAddresses = tun["excludeAddresses"]?.AsArray().Select(a => a!.GetValue<string>()).ToArray() ?? [] }
                : null,
        });
    }

    private static Profile BuildProfile(JsonObject vector)
    {
        var parsed = ShareLinkParser.Parse(vector["link"]!.GetValue<string>());
        Assert.True(parsed.IsSuccess, $"Ссылка вектора не разбирается: {parsed.Error}");
        return vector["core"]?.GetValue<string>() == "xray" ? parsed.Profile with { Core = CorePreference.Xray } : parsed.Profile;
    }

    private static string? FindSingBox()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Khors.slnx")))
            {
                var exe = OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box";
                var path = Path.Combine(dir.FullName, "cores", RuntimeInformation.RuntimeIdentifier, "sing-box", exe);
                return File.Exists(path) ? path : null;
            }
        }

        return null;
    }

    private static IEnumerable<string> VectorNames(Func<JsonObject, bool> filter) =>
        Directory.EnumerateFiles(s_directory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(n => filter(Load(n)))
            .Order(StringComparer.Ordinal);

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(s_directory, name + ".json")))!.AsObject();
}

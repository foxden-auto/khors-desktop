using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Khors.Core.Diagnostics;
using Khors.Core.Dns;
using Khors.Core.Generators;
using Khors.Core.Generators.Xray;
using Khors.Core.Import;
using Khors.Core.Profiles;
using Khors.Core.Tests.Geo;
using Xunit;

namespace Khors.Core.Tests.Generators;

/// <summary>
/// Снапшоты конфигов Xray: <c>Vectors/xray/*.json</c> — ссылка (+ <c>core</c>, <c>mux</c>), параметры запуска
/// (<c>remoteDns</c>), маршрутизация (<c>routing</c>, <see cref="RoutingVector"/>) с гео-базами (<c>geo</c>,
/// <see cref="GeoFiles.FromVector"/>) и ожидаемый конфиг (<c>expected</c>) или ошибка (<c>error</c>). Каждый ожидаемый
/// конфиг дополнительно проверяется настоящим Xray (<c>xray run -test</c>, базы — из вектора), если ядро скачано в <c>cores/</c>.
/// </summary>
public class XrayConfigVectorTests
{
    private static readonly string s_directory = Path.Combine(AppContext.BaseDirectory, "Vectors", "xray");

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
            var actual = JsonNode.Parse(result.Json);
            Assert.True(JsonNode.DeepEquals(expected, actual), $"Конфиг отличается от ожидаемого:\n{result.Json}");
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
        var json = Generate(vector).Json!;
        var masked = new SecretMasker(Encoding.UTF8.GetBytes("xray-test")).MaskJson(json);

        Assert.All(ProfileSecrets.Of(BuildProfile(vector)), s => Assert.DoesNotContain(s, masked, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Конфиг принимает настоящий Xray той версии, что закреплена в cores.lock.json.</summary>
    [Theory]
    [MemberData(nameof(SuccessVectors))]
    public async Task RealXrayAcceptsConfig(string name)
    {
        var xray = FindXray();
        Assert.SkipWhen(xray is null, "Xray не скачан: dotnet run tools/cores/fetch-cores.cs");

        var vector = Load(name);
        var assets = Directory.CreateTempSubdirectory("khors-xray-test-");
        var configPath = Path.Combine(assets.FullName, "config.json");
        await File.WriteAllTextAsync(configPath, Generate(vector).Json, TestContext.Current.CancellationToken);
        var (site, ip) = GeoFiles.FromVector(vector["geo"]);
        await File.WriteAllBytesAsync(Path.Combine(assets.FullName, "geosite.dat"), site, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(assets.FullName, "geoip.dat"), ip, TestContext.Current.CancellationToken);
        try
        {
            var startInfo = new ProcessStartInfo(xray!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            startInfo.Environment["XRAY_LOCATION_ASSET"] = assets.FullName;
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("-test");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(configPath);

            using var process = Process.Start(startInfo)!;
            var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);

            Assert.True(process.ExitCode == 0, $"xray -test: код {process.ExitCode}\n{await stdout}{await stderr}");
        }
        finally
        {
            assets.Delete(recursive: true);
        }
    }

    private static CoreConfigResult Generate(JsonObject vector)
    {
        var options = vector["options"]!;
        return XrayConfigGenerator.Generate(BuildProfile(vector), new XrayConfigOptions
        {
            SocksPort = options["socksPort"]!.GetValue<int>(),
            HttpPort = options["httpPort"]!.GetValue<int>(),
            MetricsPort = options["metricsPort"]?.GetValue<int>(),
            LogLevel = options["logLevel"]?.GetValue<string>() ?? "warning",
            Routing = RoutingVector.Plan(vector, tun: false),
            RemoteDns = options["remoteDns"] is { } remoteDns
                ? DnsServer.Parse(remoteDns.GetValue<string>()).Server ?? throw new InvalidOperationException("remoteDns вектора не разбирается")
                : DnsPresets.Default.Server,
        });
    }

    private static Profile BuildProfile(JsonObject vector)
    {
        var parsed = ShareLinkParser.Parse(vector["link"]!.GetValue<string>());
        Assert.True(parsed.IsSuccess, $"Ссылка вектора не разбирается: {parsed.Error}");
        var profile = parsed.Profile;

        if (vector["core"]?.GetValue<string>() == "singbox")
        {
            profile = profile with { Core = CorePreference.SingBox };
        }

        if (vector["mux"] is { } mux)
        {
            profile = profile with
            {
                Mux = new MuxSettings { Enabled = mux["enabled"]!.GetValue<bool>(), Concurrency = mux["concurrency"]?.GetValue<int>() },
            };
        }

        return profile;
    }

    private static string? FindXray()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Khors.slnx")))
            {
                var exe = OperatingSystem.IsWindows() ? "xray.exe" : "xray";
                var path = Path.Combine(dir.FullName, "cores", RuntimeInformation.RuntimeIdentifier, "xray", exe);
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

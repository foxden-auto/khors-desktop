using System.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Generators.SingBox;
using Khors.Core.Generators.Xray;
using Khors.Core.Import;
using Khors.Core.Profiles;

namespace Khors.Engines.Cores;

/// <summary>
/// Проверка нового ядра до включения (ROADMAP 5.2): ядро сообщает ожидаемую версию и принимает конфиги, которые KHORS
/// строит для набора вымышленных профилей (адреса из документационных диапазонов, ключи выдуманы): <c>xray run -test</c>,
/// <c>sing-box check</c>. Ловит изменения формата конфигов внутри серии; поведение при подключении так не проверить —
/// для этого остаётся откат.
/// </summary>
public static class CoreCompatibility
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(30);

    // Те же формы, что в тестовых векторах генераторов (tests/Khors.Core.Tests/Vectors), — ключи и адреса вымышленные.
    private static readonly string[] s_xrayLinks =
    [
        "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.org&fp=firefox&pbk=Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM&sid=6ba85179e30d4fc2&spx=%2F&type=tcp&headerType=none&support-x25519mlkem768=true#reality",
        "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@203.0.113.20:8443?security=reality&sni=www.example.org&fp=firefox&pbk=Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM&sid=ab&type=xhttp&path=%2Fxh&mode=stream-one#xhttp",
        "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@cdn.example.net:443?type=ws&path=%2Fws&security=tls&sni=cdn.example.net&pcs=9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08#ws",
        "trojan://p%40ss%3Aword@192.0.2.44:443?type=grpc&serviceName=tr&security=tls&sni=cdn.example.net#grpc",
        "ss://2022-blake3-aes-128-gcm:Aq3%2BZt%2F9Kp1Lm0Xy7Wq2Bw%3D%3D@198.51.100.20:8388#ss",
    ];

    private static readonly string[] s_singBoxLinks =
    [
        "hysteria2://Hy2Pa55@hy.example.com/?sni=hy.example.com#hy2",
        "tuic://8d2e6f10-4b3a-4c5d-9e8f-0a1b2c3d4e5f:TuicPa55@203.0.113.20:443?congestion_control=bbr&udp_relay_mode=quic&alpn=h3&sni=tuic.example.com#tuic",
        "wireguard://RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14%3D@198.51.100.9:51820?publickey=S2BGSyYNxTf1wij4Qk%2F8nbbFhbbGfzsDwb4shXmfImw%3D&address=10.0.0.2%2F32#wg",
    ];

    private const string AwgLink = "wireguard://RotLZicYjYUFFxy3SYlIvOJxpGmWtNthOuIC4yZdZ14%3D@198.51.100.9:51820?publickey=S2BGSyYNxTf1wij4Qk%2F8nbbFhbbGfzsDwb4shXmfImw%3D&address=10.0.0.2%2F32&jc=4&jmin=10&jmax=50&s1=1&s2=2&h1=1&h2=2&h3=3&h4=4#awg";

    /// <summary>Конфиги для проверки ядра: те же генераторы, что при подключении (в том числе TUN для sing-box).</summary>
    public static IReadOnlyList<string> Configs(CoreKind kind)
    {
        var configs = new List<string>();
        if (kind == CoreKind.Xray)
        {
            foreach (var link in s_xrayLinks)
            {
                configs.Add(Json(XrayConfigGenerator.Generate(Profile(link), new XrayConfigOptions { SocksPort = 10808, HttpPort = 10809, MetricsPort = 10810 })));
            }

            return configs;
        }

        foreach (var link in s_singBoxLinks)
        {
            configs.Add(Json(SingBoxConfigGenerator.Generate(Profile(link), new SingBoxConfigOptions
            {
                SocksPort = 10808,
                HttpPort = 10809,
                ClashApi = new ClashApiOptions(10811, "compatibility-check"),
            })));
        }

        if (kind == CoreKind.SingBoxExtended)
        {
            configs.Add(Json(SingBoxConfigGenerator.Generate(Profile(AwgLink), new SingBoxConfigOptions { SocksPort = 10808, HttpPort = 10809 })));
            configs.Add(Json(SingBoxConfigGenerator.Generate(Profile(AwgLink), new SingBoxConfigOptions { SocksPort = 10808, HttpPort = 10809, Tun = new SingBoxTunOptions() })));
        }

        // TUN (служба) и цепочка TUN → SOCKS → Xray.
        configs.Add(Json(SingBoxConfigGenerator.Generate(Profile(s_singBoxLinks[0]), new SingBoxConfigOptions { SocksPort = 10808, HttpPort = 10809, Tun = new SingBoxTunOptions() })));
        configs.Add(Json(SingBoxConfigGenerator.Generate(
            Profile(s_xrayLinks[0]) with { Core = CorePreference.Xray },
            new SingBoxConfigOptions { SocksPort = 10808, HttpPort = 10809, Tun = new SingBoxTunOptions { ExcludeAddresses = ["203.0.113.10"] }, UpstreamSocksPort = 10820 })));
        return configs;
    }

    /// <summary><c>null</c> — ядро подходит; иначе — первая строка ошибки ядра (конфиги вымышленные, секретов в ней нет).</summary>
    public static async Task<string?> CheckAsync(CoreKind kind, string executable, string expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        var version = await RunAsync(executable, ["version"], null, cancellationToken).ConfigureAwait(false);
        if (version.ExitCode != 0 || !version.Output.Contains(expectedVersion, StringComparison.Ordinal))
        {
            return "version: " + FirstLine(version.Output);
        }

        string[] arguments = kind == CoreKind.Xray ? ["run", "-test", "-c", "stdin:"] : ["check", "-c", "stdin", "--disable-color"];
        foreach (var config in Configs(kind))
        {
            var result = await RunAsync(executable, arguments, config, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                return FirstLine(result.Output);
            }
        }

        return null;
    }

    private static Profile Profile(string link) =>
        ShareLinkParser.Parse(link).Profile ?? throw new InvalidOperationException("Compatibility profile does not parse.");

    private static string Json(CoreConfigResult result) =>
        result.Json ?? throw new InvalidOperationException($"Compatibility config not generated: {result.Error}");

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 0) ?? "?";

    private static async Task<(int ExitCode, string Output)> RunAsync(string executable, string[] arguments, string? input, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Core process did not start.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(s_timeout);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), limit.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var stdout = process.StandardOutput.ReadToEndAsync(limit.Token);
            var stderr = process.StandardError.ReadToEndAsync(limit.Token);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "timeout");
        }
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Generators;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Engines.Xray;
using Xunit;

namespace Khors.Engines.Tests;

/// <summary>
/// Запуск настоящего Xray (из cores/) без выхода в интернет: профиль указывает на недоступный локальный сервер,
/// а проверяется путь данных к локальному эхо-серверу (локальные сети конфиг отправляет напрямую).
/// </summary>
public class XrayLauncherTests
{
    private const string Uuid = "3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b";

    private static readonly SecretMasker s_masker = new(Encoding.UTF8.GetBytes("engines-test"));

    // Порты выбирает ОС, чтобы тесты не конфликтовали между собой и с запущенным прокси разработчика.
    private static readonly CoreStartOptions s_anyPorts = new() { PreferredSocksPort = null, PreferredHttpPort = null };

    private static Profile LocalProfile() => new()
    {
        Id = Guid.Empty,
        Name = "local",
        Server = new ServerEndpoint("127.0.0.1", 1),
        Protocol = new VlessSettings { Id = new Secret(Uuid) },
    };

    private static void RequireXray() =>
        Assert.SkipWhen(CoreLocator.Find(CoreKind.Xray) is null, "Xray не скачан: dotnet run tools/cores/fetch-cores.cs");

    [Fact]
    public async Task XrayStartsAndPassesTrafficThroughSocksAndHttp()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var echo = new EchoServer();

        await using var session = await XrayLauncher.StartAsync(LocalProfile(), s_anyPorts, s_masker, guard: null, ct);

        Assert.Equal("ping-socks", await ProxyClient.EchoViaSocks5Async(session.SocksPort, echo.Port, "ping-socks", ct));
        Assert.Equal("ping-http", await ProxyClient.EchoViaHttpConnectAsync(session.HttpPort, echo.Port, "ping-http", ct));
    }

    [Fact]
    public async Task StopReportsExpectedExit()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        var session = await XrayLauncher.StartAsync(LocalProfile(), s_anyPorts, s_masker, guard: null, ct);

        await session.StopAsync(ct);
        var exit = await session.Process.Completion;

        Assert.True(exit.Expected);
        await session.DisposeAsync();
    }

    /// <summary>docs/SPEC.md, 5: падение ядра обнаруживается за ≤ 3 с.</summary>
    [Fact]
    public async Task CrashIsDetectedWithinThreeSeconds()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        await using var session = await XrayLauncher.StartAsync(LocalProfile(), s_anyPorts, s_masker, guard: null, ct);
        var exited = new TaskCompletionSource<CoreExit>();
        session.Process.Exited += (_, exit) => exited.TrySetResult(exit);

        var stopwatch = Stopwatch.StartNew();
        using (var external = Process.GetProcessById(session.Process.ProcessId))
        {
            external.Kill();
        }

        var exit = await exited.Task.WaitAsync(TimeSpan.FromSeconds(3), ct);

        Assert.False(exit.Expected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"Падение обнаружено за {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task BusyPreferredPortIsReplaced()
    {
        RequireXray();
        var ct = TestContext.Current.CancellationToken;
        var busy = new TcpListener(IPAddress.Loopback, 0);
        busy.Start();
        try
        {
            var busyPort = ((IPEndPoint)busy.LocalEndpoint).Port;
            var options = s_anyPorts with { PreferredSocksPort = busyPort };

            await using var session = await XrayLauncher.StartAsync(LocalProfile(), options, s_masker, guard: null, ct);

            Assert.NotEqual(busyPort, session.SocksPort);
        }
        finally
        {
            busy.Stop();
        }
    }

    [Fact]
    public async Task ProfileUnsupportedByXrayFailsBeforeStart()
    {
        RequireXray();
        var profile = LocalProfile() with { Security = new TlsSecurity { Sni = "local.example.com", AllowInsecure = true } };

        var error = await Assert.ThrowsAsync<CoreStartException>(() =>
            XrayLauncher.StartAsync(profile, s_anyPorts, s_masker, guard: null, TestContext.Current.CancellationToken));

        Assert.Equal(CoreStartFailure.ConfigNotGenerated, error.Failure);
        Assert.Equal(new CoreConfigError(CoreConfigErrorCode.UnsupportedFeature, "security.allowInsecure"), error.ConfigError);
    }

    [Fact]
    public async Task MissingExecutableIsReported()
    {
        var options = s_anyPorts with { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-xray.exe") };

        var error = await Assert.ThrowsAsync<CoreStartException>(() =>
            XrayLauncher.StartAsync(LocalProfile(), options, s_masker, guard: null, TestContext.Current.CancellationToken));

        Assert.Equal(CoreStartFailure.ExecutableNotFound, error.Failure);
    }

    [Fact]
    public async Task ConfigRejectedByCoreIsReportedWithMaskedLog()
    {
        RequireXray();
        var log = new CoreLogBuffer(s_masker);

        // Конфиг, который Xray отвергнет; адрес и UUID должны быть в логе только в замаскированном виде.
        var badConfig = $$"""{ "outbounds": [ { "protocol": "vless", "settings": { "vnext": [ { "address": "vpn.example.com", "port": 443, "users": [ { "id": "{{Uuid}}", "encryption": "none" } ] } ] } } ] }""";
        var launch = new CoreLaunch(
            CoreLocator.Find(CoreKind.Xray)!,
            ["run", "-c", "stdin:"],
            badConfig,
            new IPEndPoint(IPAddress.Loopback, PortAllocator.Allocate((int?)null)[0]),
            TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<CoreStartException>(() =>
            CoreProcess.StartAsync(launch, log, guard: null, TestContext.Current.CancellationToken));

        Assert.Equal(CoreStartFailure.ExitedDuringStart, error.Failure);
        Assert.NotEqual(0, error.ExitCode);
        Assert.Contains(error.LogTail, l => l.Contains("prohibited", StringComparison.Ordinal));
        Assert.All(log.Snapshot(), l =>
        {
            Assert.DoesNotContain(Uuid, l.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vpn.example.com", l.Text, StringComparison.OrdinalIgnoreCase);
        });
    }
}

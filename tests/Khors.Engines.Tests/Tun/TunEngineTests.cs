using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Diagnostics;
using Khors.Engines.Processes;
using Khors.Engines.Tun;
using Khors.Platform;
using Xunit;

namespace Khors.Engines.Tests.Tun;

/// <summary>
/// Режим TUN на настоящих ядрах. Без прав администратора адаптер не создаётся — проверяется путь ошибки:
/// понятная причина и отсутствие брошенных процессов. С правами (CI) тест пропускается: настоящий TUN
/// перехватил бы весь трафик машины.
/// </summary>
public class TunEngineTests
{
    private static readonly SecretMasker s_masker = new(Encoding.UTF8.GetBytes("tun-test"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireUnprivilegedWithCores()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "С правами администратора тест поднял бы настоящий TUN.");
        Assert.SkipWhen(CoreLocator.Find(CoreKind.SingBox) is null || CoreLocator.Find(CoreKind.Xray) is null, "Ядра не скачаны: dotnet run tools/cores/fetch-cores.cs");
    }

    private static Profile Hysteria2() => new()
    {
        Id = Guid.NewGuid(),
        Name = "hy2",
        Server = new ServerEndpoint("127.0.0.1", 1),
        Protocol = new Hysteria2Settings { Password = new Secret("Hy2Pa55") },
        Security = new TlsSecurity { Sni = "hy.example.com" },
    };

    private static Profile VlessReality() => new()
    {
        Id = Guid.NewGuid(),
        Name = "reality",
        Server = new ServerEndpoint("vpn.example.com", 443),
        Protocol = new VlessSettings { Id = new Secret("3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b"), Flow = "xtls-rprx-vision" },
        Security = new RealitySecurity
        {
            Sni = "www.example.org",
            PublicKey = new Secret("Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM"),
            SupportsX25519MlKem768 = true,
        },
    };

    [Fact]
    public async Task WithoutRightsTunFailsWithReason()
    {
        RequireUnprivilegedWithCores();
        var engine = new TunEngine(s_masker, guard: null);

        var error = await Assert.ThrowsAsync<CoreStartException>(() => engine.StartAsync(Hysteria2(), "warning", Ct));

        Assert.Equal(CoreStartFailure.ExitedDuringStart, error.Failure);
        Assert.Equal(CoreKind.SingBox, error.Core);
        Assert.Equal(new CoreDiagnosis(CoreProblem.TunUnavailable, CoreKind.SingBox), CoreErrorClassifier.Diagnose(CoreKind.SingBox, error.LogTail));
    }

    [Fact]
    public async Task ChainStopsXrayWhenTunFails()
    {
        RequireUnprivilegedWithCores();
        var guard = new RecordingGuard();
        var engine = new TunEngine(s_masker, guard)
        {
            Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Parse("203.0.113.10") }),
        };

        var error = await Assert.ThrowsAsync<CoreStartException>(() => engine.StartAsync(VlessReality(), "warning", Ct));

        Assert.Equal(CoreKind.SingBox, error.Core);
        Assert.Equal(2, guard.ProcessIds.Count);
        await Task.Delay(500, Ct);
        Assert.All(guard.ProcessIds, id => Assert.False(IsRunning(id), $"Процесс ядра {id} не завершён"));
    }

    [Fact]
    public async Task UnresolvedServerIsReportedBeforeStartingCores()
    {
        RequireUnprivilegedWithCores();
        var engine = new TunEngine(s_masker, guard: null)
        {
            Resolve = (_, _) => Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001)),
        };

        var error = await Assert.ThrowsAsync<CoreStartException>(() => engine.StartAsync(VlessReality(), "warning", Ct));

        Assert.Equal(new CoreDiagnosis(CoreProblem.ServerNotFound, CoreKind.Xray), error.Diagnosis);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Запоминает процессы ядер, запущенные движком.</summary>
    private sealed class RecordingGuard : IChildProcessGuard
    {
        public ConcurrentBag<int> ProcessIds { get; } = [];

        public void Attach(Process process) => ProcessIds.Add(process.Id);

        public void Dispose()
        {
        }
    }
}

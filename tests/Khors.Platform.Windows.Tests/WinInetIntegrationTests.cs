using Khors.Platform.Windows.Proxy;
using Xunit;

namespace Khors.Platform.Windows.Tests;

/// <summary>
/// Настоящий WinINet: меняет системный прокси текущего пользователя и возвращает его обратно.
/// Включается только переменной KHORS_SYSTEM_PROXY_TESTS=1 (в CI), чтобы локальный dotnet test
/// не трогал настройки разработчика.
/// </summary>
public class WinInetIntegrationTests
{
    [Fact]
    public void EnableAndRestoreRoundTripThroughRealWinInet()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("KHORS_SYSTEM_PROXY_TESTS") == "1",
            "Меняет системный прокси; включается KHORS_SYSTEM_PROXY_TESTS=1");

        var wininet = new WinInetProxySettings();
        var original = wininet.Read();
        var stateDirectory = Path.Combine(Path.GetTempPath(), "khors-wininet-test-" + Guid.NewGuid().ToString("N"));
        var proxy = new WindowsSystemProxy(wininet, stateDirectory);
        try
        {
            proxy.Enable(SystemProxySettings.ForLocalHttp(10809));

            var applied = wininet.Read();
            Assert.True(applied.UsesProxyServer);
            Assert.Equal("127.0.0.1:10809", applied.ProxyServer);

            Assert.True(proxy.Restore());
            Assert.Equal(original, wininet.Read());
        }
        finally
        {
            wininet.Write(original);
            Directory.Delete(stateDirectory, recursive: true);
        }
    }
}

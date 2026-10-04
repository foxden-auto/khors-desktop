using Khors.Platform.Windows.Proxy;
using Xunit;

namespace Khors.Platform.Windows.Tests;

/// <summary>Логика журнала и отката на подменных настройках WinINet — система не меняется.</summary>
public sealed class WindowsSystemProxyTests : IDisposable
{
    private static readonly WinInetProxyState s_userProxyWithPac = new(
        WinInetProxyState.ProxyTypeDirect | WinInetProxyState.ProxyTypeProxy | WinInetProxyState.ProxyTypeAutoProxyUrl | WinInetProxyState.ProxyTypeAutoDetect,
        "corp-proxy.example.com:3128",
        "*.example.com;<local>",
        "http://wpad.example.com/proxy.pac");

    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), "khors-proxy-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeWinInet _wininet = new(s_userProxyWithPac);

    private string JournalPath => Path.Combine(_stateDirectory, "system-proxy.json");

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
        {
            Directory.Delete(_stateDirectory, recursive: true);
        }
    }

    private WindowsSystemProxy CreateProxy() => new(_wininet, _stateDirectory);

    [Fact]
    public void EnablePointsProxyToLocalInboundAndJournalsPreviousSettings()
    {
        CreateProxy().Enable(SystemProxySettings.ForLocalHttp(10809));

        Assert.True(_wininet.State.UsesProxyServer);
        Assert.Equal("127.0.0.1:10809", _wininet.State.ProxyServer);
        Assert.Equal(0, _wininet.State.Flags & WinInetProxyState.ProxyTypeAutoProxyUrl);
        Assert.Contains("<local>", _wininet.State.ProxyBypass, StringComparison.Ordinal);
        Assert.Contains("192.168.*", _wininet.State.ProxyBypass, StringComparison.Ordinal);
        Assert.True(File.Exists(JournalPath));
    }

    [Fact]
    public void RestoreReturnsExactPreviousSettingsAndRemovesJournal()
    {
        var proxy = CreateProxy();
        proxy.Enable(SystemProxySettings.ForLocalHttp(10809));

        Assert.True(proxy.Restore());

        Assert.Equal(s_userProxyWithPac, _wininet.State);
        Assert.False(File.Exists(JournalPath));
        Assert.False(proxy.Restore());
    }

    [Fact]
    public void RepeatedEnableKeepsOriginalSettingsInJournal()
    {
        var proxy = CreateProxy();
        proxy.Enable(SystemProxySettings.ForLocalHttp(10809));
        proxy.Enable(SystemProxySettings.ForLocalHttp(20809));

        Assert.Equal("127.0.0.1:20809", _wininet.State.ProxyServer);
        proxy.Restore();
        Assert.Equal(s_userProxyWithPac, _wininet.State);
    }

    [Fact]
    public void JournalIsWrittenBeforeSettingsChange()
    {
        _wininet.FailNextWrite = true;

        Assert.Throws<InvalidOperationException>(() => CreateProxy().Enable(SystemProxySettings.ForLocalHttp(10809)));

        Assert.True(File.Exists(JournalPath));
        Assert.True(CreateProxy().Restore());
        Assert.Equal(s_userProxyWithPac, _wininet.State);
    }

    [Fact]
    public void RecoveryAfterCrashRestoresWhenProxyIsStillOurs()
    {
        // Сессия, «упавшая» без отката: новый экземпляр видит только журнал.
        CreateProxy().Enable(SystemProxySettings.ForLocalHttp(10809));

        var recovery = CreateProxy().RecoverAfterCrash();

        Assert.Equal(SystemProxyRecovery.Restored, recovery);
        Assert.Equal(s_userProxyWithPac, _wininet.State);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void RecoveryAfterCrashKeepsSettingsChangedByUser()
    {
        CreateProxy().Enable(SystemProxySettings.ForLocalHttp(10809));
        var userChoice = new WinInetProxyState(WinInetProxyState.ProxyTypeDirect, null, null, null);
        _wininet.State = userChoice;

        var recovery = CreateProxy().RecoverAfterCrash();

        Assert.Equal(SystemProxyRecovery.ChangedByUser, recovery);
        Assert.Equal(userChoice, _wininet.State);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void RecoveryWithoutJournalDoesNothing()
    {
        Assert.Equal(SystemProxyRecovery.NothingToRecover, CreateProxy().RecoverAfterCrash());
        Assert.Equal(0, _wininet.Writes);
    }

    [Fact]
    public void CorruptedJournalIsDiscardedWithoutTouchingSettings()
    {
        Directory.CreateDirectory(_stateDirectory);
        File.WriteAllText(JournalPath, "{ not json");

        Assert.Equal(SystemProxyRecovery.JournalCorrupted, CreateProxy().RecoverAfterCrash());
        Assert.Equal(s_userProxyWithPac, _wininet.State);
        Assert.False(File.Exists(JournalPath));
    }

    private sealed class FakeWinInet(WinInetProxyState initial) : IWinInetProxySettings
    {
        public WinInetProxyState State { get; set; } = initial;

        public int Writes { get; private set; }

        public bool FailNextWrite { get; set; }

        public WinInetProxyState Read() => State;

        public void Write(WinInetProxyState state)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new InvalidOperationException("Simulated WinINet failure.");
            }

            Writes++;
            State = state;
        }
    }
}

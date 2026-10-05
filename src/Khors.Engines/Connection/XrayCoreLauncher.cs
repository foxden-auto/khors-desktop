using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Engines.Xray;
using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>Запуск Xray-core (ядро по умолчанию для протоколов этапа 1).</summary>
public sealed class XrayCoreLauncher(SecretMasker masker, IChildProcessGuard? guard) : ICoreLauncher
{
    public async Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var options = new XrayStartOptions
        {
            PreferredSocksPort = preferences.SocksPort,
            PreferredHttpPort = preferences.HttpPort,
            LogLevel = preferences.LogLevel,
        };

        var session = await XrayLauncher.StartAsync(profile, options, masker, guard, cancellationToken).ConfigureAwait(false);
        return new Session(session);
    }

    private sealed class Session(XraySession session) : ICoreSession
    {
        public int SocksPort => session.SocksPort;

        public int HttpPort => session.HttpPort;

        public CoreLogBuffer Log => session.Process.Log;

        public Task<CoreExit> Completion => session.Process.Completion;

        public Task StopAsync(CancellationToken cancellationToken = default) => session.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}

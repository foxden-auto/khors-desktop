using Khors.Core.Diagnostics;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>Какое ядро запускает профиль (минимальный выбор ROADMAP 2.4; полный автовыбор — 2.5).</summary>
public static class CoreSelection
{
    /// <summary>
    /// Явный выбор пользователя; иначе Hysteria2, TUIC и WireGuard — sing-box (docs/SPEC.md, 3.4), остальное — Xray
    /// (в том числе REALITY: sing-box не отправляет X25519MLKEM768).
    /// </summary>
    public static CoreKind For(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Core switch
        {
            CorePreference.Xray => CoreKind.Xray,
            CorePreference.SingBox => CoreKind.SingBox,
            _ => profile.Protocol.HasOwnTransport ? CoreKind.SingBox : CoreKind.Xray,
        };
    }
}

/// <summary>Запуск ядра заданного вида.</summary>
public sealed class CoreKindLauncher(CoreKind kind, SecretMasker masker, IChildProcessGuard? guard) : ICoreLauncher
{
    public async Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var options = new CoreStartOptions
        {
            PreferredSocksPort = preferences.SocksPort,
            PreferredHttpPort = preferences.HttpPort,
            LogLevel = preferences.LogLevel,
        };

        var session = await CoreLauncher.StartAsync(kind, profile, options, masker, guard, cancellationToken).ConfigureAwait(false);
        return new Session(session);
    }

    private sealed class Session(CoreSession session) : ICoreSession
    {
        public CoreKind Core => session.Core;

        public int SocksPort => session.SocksPort;

        public int HttpPort => session.HttpPort;

        public CoreLogBuffer Log => session.Process.Log;

        public Task<CoreExit> Completion => session.Process.Completion;

        public Task StopAsync(CancellationToken cancellationToken = default) => session.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}

/// <summary>Запуск ядра, выбранного <see cref="CoreSelection"/>.</summary>
public sealed class SelectingCoreLauncher(ICoreLauncher xray, ICoreLauncher singBox) : ICoreLauncher
{
    public SelectingCoreLauncher(SecretMasker masker, IChildProcessGuard? guard)
        : this(new CoreKindLauncher(CoreKind.Xray, masker, guard), new CoreKindLauncher(CoreKind.SingBox, masker, guard))
    {
    }

    public Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken) =>
        (CoreSelection.For(profile) == CoreKind.SingBox ? singBox : xray).StartAsync(profile, preferences, cancellationToken);
}

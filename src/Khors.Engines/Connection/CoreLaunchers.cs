using Khors.Core.Diagnostics;
using Khors.Core.Generators.SingBox;
using Khors.Core.Generators.Xray;
using Khors.Core.Profiles;
using Khors.Engines.Processes;
using Khors.Platform;

namespace Khors.Engines.Connection;

/// <summary>Выбранное ядро профиля.</summary>
/// <param name="Automatic">Ядро выбрано автоматически (<see cref="CorePreference.Auto"/>), а не пользователем.</param>
/// <param name="Unsupported">
/// Поле профиля, которое выбранное ядро не поддерживает (запуск закончится ошибкой с объяснением);
/// <c>null</c> — ядро может запустить профиль.
/// </param>
public sealed record CoreChoice(CoreKind Core, bool Automatic, string? Unsupported)
{
    public bool IsSupported => Unsupported is null;
}

/// <summary>Какое ядро запускает профиль (docs/SPEC.md, 3.4).</summary>
public static class CoreSelection
{
    /// <summary>
    /// Явный выбор пользователя — как есть, даже если ядро не поддерживает профиль (тогда <see cref="CoreChoice.Unsupported"/>).
    /// Автовыбор: Xray, если он запускает профиль (VLESS, REALITY, XHTTP и др.; sing-box не отправляет X25519MLKEM768),
    /// иначе sing-box (Hysteria2, TUIC, WireGuard, а также то, что Xray 26 убрал: allowInsecure, VMess с alterId,
    /// плагины Shadowsocks, VLESS/Trojan без TLS). Если не может ни одно — ядро по умолчанию для протокола и его причина.
    /// </summary>
    public static CoreChoice Select(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        switch (profile.Core)
        {
            case CorePreference.Xray:
                return new CoreChoice(CoreKind.Xray, Automatic: false, XrayConfigGenerator.FindUnsupported(profile));
            case CorePreference.SingBox:
                return new CoreChoice(CoreKind.SingBox, Automatic: false, SingBoxConfigGenerator.FindUnsupported(profile));
        }

        var xray = XrayConfigGenerator.FindUnsupported(profile);
        if (xray is null)
        {
            return new CoreChoice(CoreKind.Xray, Automatic: true, null);
        }

        var singBox = SingBoxConfigGenerator.FindUnsupported(profile);
        if (singBox is null)
        {
            return new CoreChoice(CoreKind.SingBox, Automatic: true, null);
        }

        return profile.Protocol.HasOwnTransport
            ? new CoreChoice(CoreKind.SingBox, Automatic: true, singBox)
            : new CoreChoice(CoreKind.Xray, Automatic: true, xray);
    }

    public static CoreKind For(Profile profile) => Select(profile).Core;
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

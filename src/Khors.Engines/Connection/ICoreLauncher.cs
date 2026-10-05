using Khors.Core.Profiles;
using Khors.Core.Storage;
using Khors.Engines.Processes;

namespace Khors.Engines.Connection;

/// <summary>Запущенное ядро с локальными входами.</summary>
public interface ICoreSession : IAsyncDisposable
{
    CoreKind Core { get; }

    int SocksPort { get; }

    int HttpPort { get; }

    CoreLogBuffer Log { get; }

    /// <summary>Завершается, когда процесс ядра завершился.</summary>
    Task<CoreExit> Completion { get; }

    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Запуск ядра для профиля. Ошибки запуска — <see cref="CoreStartException"/>.</summary>
public interface ICoreLauncher
{
    Task<ICoreSession> StartAsync(Profile profile, CoreStartPreferences preferences, CancellationToken cancellationToken);
}

/// <summary>Пожелания к запуску из настроек пользователя.</summary>
public sealed record CoreStartPreferences(int? SocksPort = 10808, int? HttpPort = 10809, string LogLevel = "warning")
{
    public static CoreStartPreferences From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new CoreStartPreferences(settings.SocksPort, settings.HttpPort, settings.CoreLogLevel);
    }
}

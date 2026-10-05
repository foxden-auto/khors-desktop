namespace Khors.Platform;

/// <summary>
/// Один экземпляр KHORS на пользователя: второй запуск просит первый показать окно и завершается.
/// </summary>
public interface ISingleInstance : IDisposable
{
    /// <summary>Этот процесс — первый (основной) экземпляр.</summary>
    bool IsFirst { get; }

    /// <summary>Просит основной экземпляр показать окно. Вызывается вторым экземпляром.</summary>
    void SignalFirstInstance();

    /// <summary>Другой запуск попросил показать окно. Вызывается в фоновом потоке.</summary>
    event EventHandler? ActivationRequested;
}

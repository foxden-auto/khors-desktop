using System.Diagnostics;

namespace Khors.Platform;

/// <summary>
/// Привязывает дочерние процессы (ядра) к жизни приложения: при любом завершении приложения,
/// в том числе аварийном, они тоже завершаются и не держат порты (CLAUDE.md, правило 9).
/// Windows — Job Object с KILL_ON_JOB_CLOSE. <see cref="IDisposable.Dispose"/> завершает привязанные процессы.
/// </summary>
public interface IChildProcessGuard : IDisposable
{
    void Attach(Process process);
}

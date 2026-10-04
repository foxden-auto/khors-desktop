using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Khors.Platform;

namespace Khors.Engines;

/// <summary>
/// Сторож системного прокси: отдельный процесс (тот же исполняемый файл с ключом <see cref="Argument"/>),
/// который ждёт завершения KHORS и, если тот не успел откатить прокси (kill, TerminateProcess),
/// вызывает <see cref="ISystemProxy.RecoverAfterCrash"/>. При штатном выходе KHORS сам удаляет журнал,
/// и сторож завершается, ничего не меняя (CLAUDE.md, правило 9).
/// </summary>
public static class SystemProxyWatchdog
{
    public const string Argument = "--khors-system-proxy-watchdog";

    private static readonly Lock s_lock = new();
    private static Process? s_watchdog;

    /// <summary>Запускает сторожа для текущего процесса, если он ещё не запущен. Вызывать перед включением прокси.</summary>
    public static void EnsureStarted()
    {
        lock (s_lock)
        {
            if (s_watchdog is { HasExited: false })
            {
                return;
            }

            s_watchdog?.Dispose();

            using var current = Process.GetCurrentProcess();
            var startInfo = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Process path is unknown."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Запуск через «dotnet app.dll»: первым аргументом нужна сборка приложения.
            if (Path.GetFileNameWithoutExtension(startInfo.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && Assembly.GetEntryAssembly()?.Location is { Length: > 0 } entryAssembly)
            {
                startInfo.ArgumentList.Add(entryAssembly);
            }

            startInfo.ArgumentList.Add(Argument);
            startInfo.ArgumentList.Add(current.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));

            s_watchdog = Process.Start(startInfo);
        }
    }

    /// <summary>
    /// Вызывать первым делом в точке входа. Если процесс запущен как сторож — выполняет его работу и возвращает код выхода;
    /// иначе возвращает <c>null</c>, и приложение работает как обычно.
    /// </summary>
    public static async Task<int?> TryRunAsync(string[] args, Func<ISystemProxy?> createSystemProxy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(createSystemProxy);

        var at = Array.IndexOf(args, Argument);
        if (at < 0)
        {
            return null;
        }

        if (at + 2 >= args.Length
            || !int.TryParse(args[at + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var parentId)
            || !long.TryParse(args[at + 2], NumberStyles.None, CultureInfo.InvariantCulture, out var parentStartTicks))
        {
            return 2;
        }

        await WaitForExitAsync(parentId, parentStartTicks, cancellationToken).ConfigureAwait(false);

        var systemProxy = createSystemProxy();
        if (systemProxy is null)
        {
            return 0;
        }

        systemProxy.RecoverAfterCrash();
        return 0;
    }

    private static async Task WaitForExitAsync(int processId, long startTicks, CancellationToken cancellationToken)
    {
        Process parent;
        try
        {
            parent = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return; // Уже завершился.
        }

        using (parent)
        {
            try
            {
                // PID мог достаться другому процессу — тогда наш уже завершился.
                if (parent.StartTime.ToUniversalTime().Ticks != startTicks)
                {
                    return;
                }

                await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Процесс завершился между проверками.
            }
        }
    }
}

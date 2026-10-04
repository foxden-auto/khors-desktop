using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Khors.Platform;

namespace Khors.Engines.Processes;

/// <summary>
/// Запущенный процесс ядра: чтение stdout/stderr в <see cref="CoreLogBuffer"/>, ожидание готовности,
/// остановка и обнаружение падения (событие <see cref="Exited"/> приходит сразу по завершении процесса).
/// </summary>
public sealed class CoreProcess : IAsyncDisposable
{
    private static readonly TimeSpan s_stopTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_drainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_probeInterval = TimeSpan.FromMilliseconds(100);

    private readonly Process _process;
    private readonly Task _outputPump;
    private readonly TaskCompletionSource<CoreExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopRequested;

    private CoreProcess(Process process, CoreLogBuffer log)
    {
        _process = process;
        Log = log;
        ProcessId = process.Id;
        _outputPump = Task.WhenAll(
            PumpAsync(process.StandardOutput, CoreLogSource.StandardOutput),
            PumpAsync(process.StandardError, CoreLogSource.StandardError));
        _ = MonitorExitAsync();
    }

    /// <summary>Процесс ядра завершился. Вызывается в фоновом потоке.</summary>
    public event EventHandler<CoreExit>? Exited;

    public int ProcessId { get; }

    public CoreLogBuffer Log { get; }

    /// <summary>Завершается, когда процесс ядра завершился.</summary>
    public Task<CoreExit> Completion => _exit.Task;

    public bool HasExited => _exit.Task.IsCompleted;

    public static async Task<CoreProcess> StartAsync(
        CoreLaunch launch,
        CoreLogBuffer log,
        IChildProcessGuard? guard,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(log);

        if (!File.Exists(launch.ExecutablePath))
        {
            throw new CoreStartException(CoreStartFailure.ExecutableNotFound, $"Core executable not found: {Path.GetFileName(launch.ExecutablePath)}");
        }

        var startInfo = new ProcessStartInfo(launch.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = launch.StandardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(launch.ExecutablePath)!,
        };
        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var core = new CoreProcess(process, log);

        try
        {
            guard?.Attach(process);

            if (launch.StandardInput is not null)
            {
                await process.StandardInput.WriteAsync(launch.StandardInput.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await core.WaitReadyAsync(launch.ReadinessEndpoint, launch.ReadyTimeout, cancellationToken).ConfigureAwait(false);
            return core;
        }
        catch
        {
            await core.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Останавливает ядро. После этого <see cref="Exited"/> приходит с <c>Expected = true</c>.</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _stopRequested, 1);
        if (!HasExited)
        {
            try
            {
                // Ядрам нечего сохранять при выходе, поэтому сразу завершаем процесс.
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Процесс уже завершился.
            }
        }

        await _exit.Task.WaitAsync(s_stopTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _process.Dispose();
        }
    }

    private async Task WaitReadyAsync(IPEndPoint endpoint, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            if (_exit.Task.IsCompleted)
            {
                var exit = await _exit.Task.ConfigureAwait(false);
                throw new CoreStartException(
                    CoreStartFailure.ExitedDuringStart,
                    $"Core exited during start with code {exit.ExitCode}.",
                    exit.ExitCode,
                    Log.Tail(20));
            }

            if (await CanConnectAsync(endpoint, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            if (deadline.Elapsed > timeout)
            {
                throw new CoreStartException(CoreStartFailure.ReadyTimeout, $"Core did not open {endpoint} within {timeout.TotalSeconds:0} s.", logTail: Log.Tail(20));
            }

            await Task.WhenAny(_exit.Task, Task.Delay(s_probeInterval, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static async Task<bool> CanConnectAsync(IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(endpoint.AddressFamily);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromMilliseconds(500));
        try
        {
            await client.ConnectAsync(endpoint, attempt.Token).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task PumpAsync(StreamReader reader, CoreLogSource source)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.Length > 0)
            {
                Log.Add(source, line);
            }
        }
    }

    private async Task MonitorExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);

        // Дочитываем лог, чтобы последние строки (причина падения) были в буфере до события.
        await Task.WhenAny(_outputPump, Task.Delay(s_drainTimeout)).ConfigureAwait(false);

        var exit = new CoreExit(_process.ExitCode, Volatile.Read(ref _stopRequested) == 1, DateTimeOffset.Now);
        _exit.TrySetResult(exit);
        Exited?.Invoke(this, exit);
    }
}

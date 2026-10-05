using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Khors.Core.Profiles;
using Khors.Engines.Connection;
using Khors.Engines.Processes;

namespace Khors.Engines.Latency;

public enum LatencyStatus
{
    Ok,

    /// <summary>Ответа не было за отведённое время.</summary>
    Timeout,

    /// <summary>Соединение отклонено, ошибка протокола или неожиданный ответ.</summary>
    Failed,

    /// <summary>Ядро не может запустить профиль (ошибки профиля, неподдерживаемая возможность, нет ядра).</summary>
    Unsupported,
}

/// <param name="Delay">
/// Задержка через туннель: время запроса по уже установленному соединению — близко к «пингу через VPN».
/// </param>
/// <param name="FirstConnection">Время первого запроса с установкой туннеля и TLS (несколько круговых задержек).</param>
public sealed record LatencyResult(LatencyStatus Status, TimeSpan? Delay = null, TimeSpan? FirstConnection = null)
{
    public static LatencyResult Success(TimeSpan delay, TimeSpan? firstConnection = null) => new(LatencyStatus.Ok, delay, firstConnection);
}

/// <summary>
/// Тест задержки (docs/SPEC.md, 4.7): TCP-подключение к серверу и настоящий запрос через прокси.
/// Сетевые запросы — только к адресу теста задержки (CLAUDE.md, правило 6).
/// </summary>
public static class LatencyTester
{
    /// <summary>Адрес по умолчанию: отвечает 204 без содержимого.</summary>
    public static Uri DefaultTestUrl { get; } = new("https://cp.cloudflare.com/generate_204");

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Время TCP-подключения к серверу (без ядра). Разрешение имени в замер не входит.</summary>
    public static async Task<LatencyResult> MeasureTcpAsync(ServerEndpoint server, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(server.Host, limit.Token).ConfigureAwait(false);
            if (addresses.Length == 0)
            {
                return new LatencyResult(LatencyStatus.Failed);
            }

            using var client = new TcpClient(addresses[0].AddressFamily);
            var stopwatch = Stopwatch.StartNew();
            await client.ConnectAsync(addresses[0], server.Port, limit.Token).ConfigureAwait(false);
            return LatencyResult.Success(stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LatencyResult(LatencyStatus.Timeout);
        }
        catch (SocketException)
        {
            return new LatencyResult(LatencyStatus.Failed);
        }
    }

    /// <summary>
    /// Задержка через HTTP-вход ядра на 127.0.0.1: два запроса <paramref name="url"/> по одному соединению.
    /// Первый устанавливает туннель (TCP, REALITY/TLS до сервера, TLS до адреса теста), второй идёт по готовому —
    /// его время и есть задержка. Если второй не удался, задержкой считается первый.
    /// </summary>
    public static async Task<LatencyResult> MeasureThroughProxyAsync(int httpPort, Uri url, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var handler = new HttpClientHandler
        {
            Proxy = new AlwaysProxy(new Uri($"http://127.0.0.1:{httpPort}")),
            UseProxy = true,
            AllowAutoRedirect = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);

        try
        {
            var first = await TimeRequestAsync(client, url, limit.Token).ConfigureAwait(false);
            if (first is null)
            {
                return new LatencyResult(LatencyStatus.Failed);
            }

            TimeSpan? warm = null;
            try
            {
                warm = await TimeRequestAsync(client, url, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Второй запрос не обязателен: остаётся время первого.
            }

            return LatencyResult.Success(warm ?? first.Value, first);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new LatencyResult(LatencyStatus.Timeout);
        }
        catch (HttpRequestException)
        {
            return new LatencyResult(LatencyStatus.Failed);
        }
    }

    /// <summary>Время до получения заголовков ответа; <c>null</c> — ответ не 204/200.</summary>
    private static async Task<TimeSpan?> TimeRequestAsync(HttpClient client, Uri url, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var elapsed = stopwatch.Elapsed;
        await response.Content.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
        return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK ? elapsed : null;
    }

    /// <summary>Прокси без исключений: стандартный WebProxy пропускает адреса loopback мимо прокси.</summary>
    private sealed class AlwaysProxy(Uri proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri GetProxy(Uri destination) => proxy;

        public bool IsBypassed(Uri host) => false;
    }

    /// <summary>
    /// Настоящая задержка профиля, который сейчас не подключён: временное ядро на свободных портах
    /// (системный прокси не меняется), один запрос, остановка ядра.
    /// </summary>
    public static async Task<LatencyResult> MeasureProfileAsync(
        Profile profile,
        ICoreLauncher launcher,
        Uri url,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(launcher);

        if (ProfileValidator.Validate(profile).Any(i => i.Severity == ProfileIssueSeverity.Error))
        {
            return new LatencyResult(LatencyStatus.Unsupported);
        }

        ICoreSession session;
        try
        {
            session = await launcher.StartAsync(profile, new CoreStartPreferences(SocksPort: null, HttpPort: null), cancellationToken).ConfigureAwait(false);
        }
        catch (CoreStartException ex)
        {
            return new LatencyResult(ex.Failure is CoreStartFailure.ExitedDuringStart or CoreStartFailure.ReadyTimeout
                ? LatencyStatus.Failed
                : LatencyStatus.Unsupported);
        }

        await using (session.ConfigureAwait(false))
        {
            return await MeasureThroughProxyAsync(session.HttpPort, url, timeout, cancellationToken).ConfigureAwait(false);
        }
    }
}

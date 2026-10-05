using System.Net;
using System.Reflection;
using System.Text;
using Khors.Core.Subscriptions;

namespace Khors.Engines.Subscriptions;

/// <summary>Ответ сервера подписки или причина неудачи.</summary>
public sealed record SubscriptionFetchResult(
    SubscriptionUpdateError? Error,
    string? Body = null,
    SubscriptionUserInfo? UserInfo = null,
    int? UpdateIntervalHours = null,
    string? Title = null,
    int? HttpStatus = null);

/// <summary>
/// Загрузка подписки — один из разрешённых сетевых запросов (CLAUDE.md, правило 6).
/// Без явного прокси идёт в обход системного прокси, чтобы при подключённом KHORS не ходить через самого себя.
/// </summary>
public static class SubscriptionFetcher
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);

    public static string DefaultUserAgent { get; } =
        $"KHORS-Desktop/{Assembly.GetEntryAssembly()?.GetName().Version?.ToString(2) ?? "1.0"}";

    /// <param name="proxy">Прокси (например, локальный вход KHORS); <c>null</c> — напрямую.</param>
    public static async Task<SubscriptionFetchResult> FetchAsync(Uri url, string userAgent, IWebProxy? proxy, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrEmpty(userAgent);

        using var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            UseProxy = proxy is not null,
            Proxy = proxy,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new SubscriptionFetchResult(SubscriptionUpdateError.HttpError, HttpStatus: (int)response.StatusCode);
            }

            var body = await ReadLimitedAsync(response.Content, limit.Token).ConfigureAwait(false);
            if (body is null)
            {
                return new SubscriptionFetchResult(SubscriptionUpdateError.TooLarge, HttpStatus: (int)response.StatusCode);
            }

            return new SubscriptionFetchResult(
                null,
                body,
                SubscriptionHeaders.ParseUserInfo(Header(response, "subscription-userinfo")),
                SubscriptionHeaders.ParseUpdateInterval(Header(response, "profile-update-interval")),
                SubscriptionHeaders.ParseTitle(Header(response, "profile-title"), response.Content.Headers.ContentDisposition?.ToString()),
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubscriptionFetchResult(SubscriptionUpdateError.Timeout);
        }
        catch (HttpRequestException)
        {
            return new SubscriptionFetchResult(SubscriptionUpdateError.Network);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? values.FirstOrDefault()
            : null;

    /// <summary>Тело ответа как UTF-8, не больше <see cref="MaxBytes"/>; <c>null</c> — превышен лимит.</summary>
    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }
}

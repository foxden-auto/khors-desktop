using System.Net;
using Khors.Engines.Subscriptions;

namespace Khors.Engines.Geo;

public enum GeoUpdateError
{
    /// <summary>Сеть недоступна или соединение оборвалось.</summary>
    Network,

    Timeout,

    /// <summary>Сервер ответил ошибкой HTTP.</summary>
    HttpError,

    /// <summary>Файл больше допустимого.</summary>
    TooLarge,

    /// <summary>Скачанный файл не совпал с контрольной суммой релиза.</summary>
    ChecksumMismatch,

    /// <summary>Файл не разбирается или в нём нет нужных категорий.</summary>
    Invalid,

    /// <summary>Не удалось записать файл на диск.</summary>
    WriteFailed,
}

/// <summary>Содержимое по адресу или причина неудачи.</summary>
public sealed record GeoFetchResult(byte[]? Data, GeoUpdateError? Error)
{
    public override string ToString() => $"GeoFetchResult {{ Bytes = {Data?.Length}, Error = {Error} }}";
}

/// <summary>Загрузка гео-баз — разрешённый сетевой запрос (CLAUDE.md, правило 6). Без прокси — в обход системного прокси.</summary>
public static class GeoFetcher
{
    public static async Task<GeoFetchResult> FetchAsync(Uri url, IWebProxy? proxy, long maxBytes, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

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
        request.Headers.TryAddWithoutValidation("User-Agent", SubscriptionFetcher.DefaultUserAgent);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new GeoFetchResult(null, GeoUpdateError.HttpError);
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                return new GeoFetchResult(null, GeoUpdateError.TooLarge);
            }

            var stream = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, limit.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        return new GeoFetchResult(null, GeoUpdateError.TooLarge);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return new GeoFetchResult(buffer.ToArray(), null);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new GeoFetchResult(null, GeoUpdateError.Timeout);
        }
        catch (HttpRequestException)
        {
            return new GeoFetchResult(null, GeoUpdateError.Network);
        }
        catch (IOException)
        {
            return new GeoFetchResult(null, GeoUpdateError.Network);
        }
    }
}

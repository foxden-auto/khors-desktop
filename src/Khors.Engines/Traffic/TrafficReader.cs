using System.Net.Http.Headers;
using System.Text.Json;

namespace Khors.Engines.Traffic;

/// <summary>Сколько байт отправлено на сервер и получено с него с запуска ядра.</summary>
public sealed record TrafficCounters(long Uplink, long Downlink);

/// <summary>Где ядро отдаёт счётчики: Xray — <c>metrics</c>, sing-box — Clash API (с секретом). Только 127.0.0.1.</summary>
public sealed record TrafficEndpoint(CoreKind Core, int Port, string? Secret = null)
{
    // Секрет Clash API — в журнал не выводим.
    public override string ToString() => $"TrafficEndpoint {{ Core = {Core}, Port = {Port} }}";
}

/// <summary>
/// Чтение счётчиков трафика у запущенного ядра по HTTP на loopback (CLAUDE.md, правило 6: в сеть не ходит).
/// Xray: <c>/debug/vars</c> → <c>stats.outbound.proxy</c> (только трафик через сервер). sing-box: <c>/connections</c> →
/// <c>uploadTotal</c>/<c>downloadTotal</c> (все соединения ядра).
/// </summary>
public static class TrafficReader
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    // Мимо системного прокси: он указывает на само ядро.
    private static readonly HttpClient s_http = new(new SocketsHttpHandler { UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1) })
    {
        Timeout = s_timeout,
    };

    /// <summary>Счётчики или <c>null</c>, если ядро не ответило или ответ не разобран.</summary>
    public static async Task<TrafficCounters?> ReadAsync(TrafficEndpoint endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var path = endpoint.Core == CoreKind.Xray ? "/debug/vars" : "/connections";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://127.0.0.1:{endpoint.Port}{path}"));
        if (endpoint.Secret is { } secret)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        try
        {
            using var response = await s_http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return endpoint.Core == CoreKind.Xray ? ParseXray(json) : ParseSingBox(json);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Ответ <c>/debug/vars</c> Xray; счётчиков ещё нет (не было трафика) — нули.</summary>
    public static TrafficCounters? ParseXray(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var proxy = document.RootElement.TryGetProperty("stats", out var stats)
                && stats.TryGetProperty("outbound", out var outbound)
                && outbound.TryGetProperty("proxy", out var p) ? p : default;
            return new TrafficCounters(Number(proxy, "uplink"), Number(proxy, "downlink"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ответ <c>/connections</c> Clash API sing-box.</summary>
    public static TrafficCounters? ParseSingBox(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("uploadTotal", out _)
                ? new TrafficCounters(Number(root, "uploadTotal"), Number(root, "downloadTotal"))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) && number >= 0
            ? number
            : 0;
}

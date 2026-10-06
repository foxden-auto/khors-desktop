namespace Khors.Core.Dns;

/// <summary>Готовый удалённый DNS: провайдер и адрес. Название провайдера — имя собственное, не переводится.</summary>
public sealed record DnsPreset(string Id, string Provider, DnsServer Server);

/// <summary>
/// Пресеты удалённого DNS (ROADMAP 3.4). Адреса — IP: сертификаты DoH/DoT этих провайдеров выписаны и на IP,
/// поэтому самому DNS-серверу не нужно разрешение имени до подключения (проверено 2026-10-06).
/// </summary>
public static class DnsPresets
{
    public static IReadOnlyList<DnsPreset> All { get; } =
    [
        Preset("cloudflare-doh", "Cloudflare", "https://1.1.1.1/dns-query"),
        Preset("cloudflare-dot", "Cloudflare", "tls://1.1.1.1"),
        Preset("google-doh", "Google", "https://8.8.8.8/dns-query"),
        Preset("google-dot", "Google", "tls://8.8.8.8"),
        Preset("quad9-doh", "Quad9", "https://9.9.9.9/dns-query"),
        Preset("quad9-dot", "Quad9", "tls://9.9.9.9"),
        Preset("adguard-doh", "AdGuard", "https://94.140.14.14/dns-query"),
        Preset("adguard-dot", "AdGuard", "tls://94.140.14.14"),
    ];

    /// <summary>По умолчанию — DoH Cloudflare (так было до появления настройки).</summary>
    public static DnsPreset Default => All[0];

    /// <summary>Пресет с тем же адресом; <c>null</c> — адрес свой.</summary>
    public static DnsPreset? Find(DnsServer server) => All.FirstOrDefault(p => p.Server == server);

    /// <summary>
    /// Сервер из текста настроек; пустой или неверный текст (файл правили руками) — сервер по умолчанию,
    /// чтобы подключение не ломалось из-за настройки.
    /// </summary>
    public static DnsServer ServerOrDefault(string? text) =>
        DnsServer.Parse(text) is { IsSuccess: true } parsed ? parsed.Server : Default.Server;

    private static DnsPreset Preset(string id, string provider, string address) =>
        new(id, provider, DnsServer.Parse(address).Server ?? throw new InvalidOperationException(address));
}

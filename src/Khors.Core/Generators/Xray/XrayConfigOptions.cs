namespace Khors.Core.Generators.Xray;

/// <summary>Параметры запуска, не относящиеся к профилю. Порты выбирает Khors.Engines (ROADMAP 1.4).</summary>
public sealed record XrayConfigOptions
{
    /// <summary>Адрес локальных входов. Только loopback: прокси не должен быть доступен из сети.</summary>
    public string ListenAddress { get; init; } = "127.0.0.1";

    public required int SocksPort { get; init; }

    public required int HttpPort { get; init; }

    /// <summary>Уровень лога Xray: debug, info, warning, error, none.</summary>
    public string LogLevel { get; init; } = "warning";

    /// <summary>Порт API статистики; <c>null</c> — API выключен.</summary>
    public int? ApiPort { get; init; }
}

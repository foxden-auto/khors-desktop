namespace Khors.Engines.Diagnostics;

/// <summary>Уровень лога, с которым запускается ядро, и какие строки попадают в буфер.</summary>
/// <param name="Level">Уровень в терминах Xray (debug, info, warning, error, none).</param>
/// <param name="KeepLine">Фильтр сырых строк до маскировки и записи в буфер; <c>null</c> — все строки.</param>
public sealed record CoreLogPlan(string Level, Func<string, bool>? KeepLine);

public static class CoreLogLevels
{
    /// <summary>
    /// Xray пишет неудачные соединения с сервером (отказ, тайм-аут, ошибки сертификата) только на уровне info,
    /// поэтому при warning и error ядро запускается с info, а обычные строки info о каждом соединении
    /// (запрос, маршрут, dialing, tunneling — в них адреса посещаемых сайтов) отбрасываются до записи в буфер.
    /// Остаются ошибки исходящих соединений — их разбирает <see cref="CoreErrorClassifier"/>.
    /// Явно выбранные info и debug — как есть (режим разработчика), sing-box пишет ошибки уже на warn.
    /// </summary>
    public static CoreLogPlan Plan(CoreKind core, string level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (core == CoreKind.Xray && level is "warning" or "error")
        {
            return new CoreLogPlan("info", KeepXrayLine);
        }

        return new CoreLogPlan(level, null);
    }

    internal static bool KeepXrayLine(string line)
    {
        if (!line.Contains("[Info]", StringComparison.Ordinal) && !line.Contains("[Debug]", StringComparison.Ordinal))
        {
            return true;
        }

        return line.Contains("failed", StringComparison.OrdinalIgnoreCase) && line.Contains("outbound", StringComparison.Ordinal);
    }
}

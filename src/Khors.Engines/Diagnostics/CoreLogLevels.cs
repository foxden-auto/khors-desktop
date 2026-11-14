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
    /// sing-box-extended пишет тайм-аут рукопожатия WireGuard на уровне debug.
    /// При warning и error сохраняем это сообщение и отбрасываем остальные строки debug и info.
    /// </summary>
    public static CoreLogPlan Plan(CoreKind core, string level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (core == CoreKind.Xray && level is "warning" or "error")
        {
            return new CoreLogPlan("info", KeepXrayLine);
        }

        if (core == CoreKind.SingBoxExtended && level is "warning" or "error")
        {
            return new CoreLogPlan("debug", line =>
                KeepExtendedLine(line, includeWarnings: level == "warning"));
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

    private static bool KeepExtendedLine(string line, bool includeWarnings) =>
        HasSingBoxLevel(line, "FATAL")
        || HasSingBoxLevel(line, "PANIC")
        || HasSingBoxLevel(line, "ERROR")
        || (includeWarnings && HasSingBoxLevel(line, "WARN"))
        || CoreErrorClassifier.IsWireGuardHandshakeTimeout(line);

    // С timestamp ядро печатает "+0300 yyyy-MM-dd HH:mm:ss LEVEL ...".
    private static bool HasSingBoxLevel(string line, string level) =>
        line.StartsWith(level + "[", StringComparison.Ordinal)
        || (line.Length > 26 + level.Length
            && line[0] is '+' or '-'
            && line.AsSpan(26).StartsWith(level + " ", StringComparison.Ordinal));
}

namespace Khors.Engines.Diagnostics;

/// <summary>Известная причина неудачи по логу ядра (docs/SPEC.md, 4.7).</summary>
public enum CoreProblem
{
    /// <summary>Ядро отвергло конфиг при запуске.</summary>
    ConfigRejected,

    /// <summary>Локальный порт занят другой программой.</summary>
    PortInUse,

    /// <summary>Сервер ответил как сайт-маскировка: не принял REALITY-рукопожатие.</summary>
    RealityRejected,

    /// <summary>Сервер отклонил пароль (Hysteria2, TUIC).</summary>
    AuthenticationFailed,

    /// <summary>Сертификат сервера просрочен или ещё не действует — часто неверные часы компьютера.</summary>
    CertificateExpired,

    /// <summary>Сертификат выдан на другое имя: неверный SNI.</summary>
    CertificateNameMismatch,

    /// <summary>Сертификат не доверенный (самоподписанный или подмена).</summary>
    CertificateUntrusted,

    /// <summary>TLS-рукопожатие с сервером не удалось.</summary>
    TlsHandshakeFailed,

    /// <summary>Имя сервера не разрешилось.</summary>
    ServerNotFound,

    /// <summary>На порту сервера никто не принимает соединения.</summary>
    ConnectionRefused,

    /// <summary>Сервер не ответил вовремя.</summary>
    ConnectionTimeout,

    /// <summary>Соединение с сервером сброшено — сервер или блокировка по пути.</summary>
    ConnectionReset,

    /// <summary>Нет маршрута до сервера.</summary>
    NetworkUnreachable,
}

/// <summary>Причина неудачи и ядро, в логе которого она найдена (от ядра зависит подсказка).</summary>
public sealed record CoreDiagnosis(CoreProblem Problem, CoreKind Core);

/// <summary>
/// Классификатор строк лога Xray и sing-box. Строки уже замаскированы: признаки — тексты ошибок ядер и Go
/// (Linux и Windows), а не адреса. Сетевые ошибки засчитываются, только если это неудачное соединение
/// с сервером профиля: ошибки прямых соединений (локальная сеть) и обрывы уже работающих соединений не в счёт.
/// </summary>
public static class CoreErrorClassifier
{
    // Порядок важен: частные признаки раньше общих (просроченный сертификат раньше «failed to verify certificate»).
    private static readonly (CoreProblem Problem, string[] Markers)[] s_dialRules =
    [
        (CoreProblem.AuthenticationFailed, ["authentication failed"]),
        (CoreProblem.CertificateExpired, ["certificate has expired or is not yet valid"]),
        (CoreProblem.CertificateNameMismatch, ["certificate is valid for", "certificate is not valid for any names"]),
        (CoreProblem.CertificateUntrusted, ["certificate signed by unknown authority", "failed to verify certificate", "certificate is not trusted"]),
        (CoreProblem.TlsHandshakeFailed, ["tls: handshake failure", "remote error: tls:", "first record does not look like a TLS handshake"]),
        (CoreProblem.ServerNotFound, ["no such host"]),
        (CoreProblem.ConnectionRefused, ["connection refused", "actively refused"]),
        (CoreProblem.ConnectionTimeout, ["i/o timeout", "did not properly respond", "context deadline exceeded", "no recent network activity", "handshake did not complete in time"]),
        (CoreProblem.ConnectionReset, ["connection reset by peer", "forcibly closed by the remote host"]),
        (CoreProblem.NetworkUnreachable, ["network is unreachable", "no route to host", "unreachable network", "unreachable host"]),
    ];

    /// <summary>Причина по одной строке лога; <c>null</c> — строка не говорит о неудаче.</summary>
    public static CoreProblem? Classify(CoreKind core, string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        // Xray 26 предупреждает об устаревших протоколах и транспортах — это не сбой.
        if (Has(line, "deprecated"))
        {
            return null;
        }

        if (Has(line, "address already in use") || Has(line, "only one usage of each socket address"))
        {
            return CoreProblem.PortInUse;
        }

        if (line.StartsWith("Failed to start:", StringComparison.Ordinal) || line.StartsWith("FATAL[", StringComparison.Ordinal))
        {
            return CoreProblem.ConfigRejected;
        }

        // Xray: «REALITY: received real certificate» ([Error]); sing-box: «reality verification failed».
        if (Has(line, "REALITY: received real certificate") || Has(line, "REALITY: processed invalid connection") || Has(line, "reality verification failed"))
        {
            return CoreProblem.RealityRejected;
        }

        if (!IsServerDialFailure(core, line))
        {
            return null;
        }

        foreach (var (problem, markers) in s_dialRules)
        {
            if (markers.Any(m => Has(line, m)))
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Последняя известная причина в строках лога (от новых к старым).</summary>
    public static CoreDiagnosis? Diagnose(CoreKind core, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var line in lines.Reverse())
        {
            if (Classify(core, line) is { } problem)
            {
                return new CoreDiagnosis(problem, core);
            }
        }

        return null;
    }

    /// <summary>
    /// Неудачное соединение с сервером профиля. Xray: «failed to find an available destination» — все попытки
    /// подключения (TCP, TLS/REALITY) не удались; прямые соединения идут через freedom без этой фразы.
    /// sing-box: «open connection … using outbound/…» кроме прямого выхода и блокировки.
    /// </summary>
    private static bool IsServerDialFailure(CoreKind core, string line) => core switch
    {
        CoreKind.Xray => Has(line, "failed to find an available destination"),
        CoreKind.SingBox => (Has(line, "open connection") || Has(line, "open packet connection"))
            && Has(line, "outbound/")
            && !Has(line, "outbound/direct")
            && !Has(line, "outbound/block"),
        _ => false,
    };

    private static bool Has(string line, string marker) => line.Contains(marker, StringComparison.OrdinalIgnoreCase);
}

namespace Khors.Engines.Connection;

/// <summary>Когда и как часто переподключаться (docs/SPEC.md, 4.3; ROADMAP 4.3).</summary>
public static class ReconnectPolicy
{
    /// <summary>Самая длинная пауза между попытками.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    /// <summary>Подключение, продержавшееся столько, считается восстановленным: следующий обрыв — снова с первой попытки.</summary>
    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(60);

    /// <summary>Пауза перед попыткой: 1, 2, 4, 8… с, не больше <see cref="MaxDelay"/>.</summary>
    public static TimeSpan DelayBefore(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(attempt);
        return attempt > 7 ? MaxDelay : TimeSpan.FromSeconds(Math.Min(MaxDelay.TotalSeconds, 1 << (attempt - 1)));
    }

    /// <summary>
    /// Может ли повтор помочь: неверный профиль, неподдерживаемая ядром возможность и отсутствующий файл ядра
    /// сами не исправятся.
    /// </summary>
    public static bool IsRetryable(ConnectionFailureKind kind) =>
        kind is not (ConnectionFailureKind.ProfileInvalid or ConnectionFailureKind.UnsupportedByCore or ConnectionFailureKind.CoreNotFound);
}

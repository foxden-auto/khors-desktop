namespace Khors.Platform;

/// <summary>Сон и пробуждение компьютера (ROADMAP 4.3).</summary>
public interface IPowerEvents : IDisposable
{
    /// <summary>Компьютер проснулся. Приходит в фоновом потоке; сеть в этот момент может быть ещё не готова.</summary>
    event EventHandler? Resumed;
}

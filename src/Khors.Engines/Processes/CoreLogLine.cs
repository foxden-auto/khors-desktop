namespace Khors.Engines.Processes;

public enum CoreLogSource
{
    StandardOutput,
    StandardError,
}

/// <summary>Строка лога ядра. Текст уже замаскирован (CLAUDE.md, правило 5).</summary>
public sealed record CoreLogLine(DateTimeOffset Time, CoreLogSource Source, string Text);

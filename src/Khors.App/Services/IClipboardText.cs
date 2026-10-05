namespace Khors.App.Services;

/// <summary>Текст в буфере обмена (реализация — через окно Avalonia).</summary>
public interface IClipboardText
{
    Task<string?> GetTextAsync();

    Task SetTextAsync(string text);
}

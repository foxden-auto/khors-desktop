namespace Khors.App.Services;

/// <summary>Чтение текста из буфера обмена (реализация — через окно Avalonia).</summary>
public interface IClipboardText
{
    Task<string?> GetTextAsync();
}

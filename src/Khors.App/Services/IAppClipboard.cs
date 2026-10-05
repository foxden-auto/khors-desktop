using Avalonia.Media.Imaging;

namespace Khors.App.Services;

/// <summary>Буфер обмена: текст и картинки (реализация — через окно Avalonia).</summary>
public interface IAppClipboard
{
    Task<string?> GetTextAsync();

    Task SetTextAsync(string text);

    /// <summary>Картинка из буфера; <c>null</c> — картинки нет. Освобождает вызывающий.</summary>
    Task<Bitmap?> GetBitmapAsync();

    Task SetBitmapAsync(Bitmap bitmap);
}

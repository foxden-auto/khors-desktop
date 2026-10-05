using Avalonia.Media.Imaging;

namespace Khors.App.Services;

/// <summary>Выбранный файл для импорта: имя (для типа) и содержимое.</summary>
public sealed record PickedFile(string Name, byte[] Content)
{
    private static readonly string[] s_imageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];

    public bool IsImage => s_imageExtensions.Contains(Path.GetExtension(Name), StringComparer.OrdinalIgnoreCase);
}

/// <summary>Диалоги окна: выбор файла, окно с QR-кодом.</summary>
public interface IDesktopDialogs
{
    /// <summary>Файл с QR-кодом, ссылками или конфигом; <c>null</c> — отменено. Больше лимита — исключение <see cref="FileTooLargeException"/>.</summary>
    Task<PickedFile?> PickImportFileAsync();

    /// <summary>Окно с QR-кодом; картинку освобождает окно при закрытии.</summary>
    Task ShowQrAsync(string profileName, Bitmap qr);
}

public sealed class FileTooLargeException() : IOException("The file is larger than the import limit.");

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Khors.App.ViewModels;
using Khors.App.Views;

namespace Khors.App.Services;

/// <summary>Диалоги поверх главного окна.</summary>
public sealed class WindowDialogs(Func<Window?> owner, IAppClipboard clipboard) : IDesktopDialogs
{
    /// <summary>Как у подписок: больше — не конфиг и не картинка с QR.</summary>
    private const long MaxFileBytes = 5 * 1024 * 1024;

    public async Task<PickedFile?> PickImportFileAsync()
    {
        if (owner() is not { } window)
        {
            return null;
        }

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localizer.Get("ImportFileDialogTitle"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Localizer.Get("FileTypeImportable"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.txt", "*.yaml", "*.yml", "*.json", "*.conf"],
                },
                FilePickerFileTypes.All,
            ],
        }).ConfigureAwait(true);

        if (files.Count == 0)
        {
            return null;
        }

        await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
        using var content = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer).ConfigureAwait(true)) > 0)
        {
            if (content.Length + read > MaxFileBytes)
            {
                throw new FileTooLargeException();
            }

            content.Write(buffer, 0, read);
        }

        return new PickedFile(files[0].Name, content.ToArray());
    }

    public async Task ShowQrAsync(string profileName, Bitmap qr)
    {
        var dialog = new QrWindow { DataContext = new QrWindowViewModel(profileName, qr, clipboard) };
        dialog.Closed += (_, _) => qr.Dispose();
        if (owner() is { IsVisible: true } window)
        {
            await dialog.ShowDialog(window).ConfigureAwait(true);
        }
        else
        {
            dialog.Show();
        }
    }
}

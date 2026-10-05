using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace Khors.App.Services;

/// <summary>Буфер обмена через окно Avalonia.</summary>
public sealed class WindowClipboard(Func<TopLevel?> topLevel) : IAppClipboard
{
    public async Task<string?> GetTextAsync() =>
        topLevel()?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync().ConfigureAwait(true) : null;

    public async Task SetTextAsync(string text)
    {
        if (topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text).ConfigureAwait(true);
        }
    }

    public async Task<Bitmap?> GetBitmapAsync() =>
        topLevel()?.Clipboard is { } clipboard ? await clipboard.TryGetBitmapAsync().ConfigureAwait(true) : null;

    public async Task SetBitmapAsync(Bitmap bitmap)
    {
        if (topLevel()?.Clipboard is { } clipboard)
        {
            await clipboard.SetBitmapAsync(bitmap).ConfigureAwait(true);
        }
    }
}

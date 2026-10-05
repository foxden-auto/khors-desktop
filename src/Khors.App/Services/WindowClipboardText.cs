using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Khors.App.Services;

/// <summary>Буфер обмена через окно Avalonia.</summary>
public sealed class WindowClipboardText(Func<TopLevel?> topLevel) : IClipboardText
{
    public async Task<string?> GetTextAsync() =>
        topLevel()?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync().ConfigureAwait(true) : null;
}

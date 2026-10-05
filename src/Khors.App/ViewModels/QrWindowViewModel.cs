using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Khors.App.Services;

namespace Khors.App.ViewModels;

/// <summary>Окно с QR-кодом профиля. В коде — ключи доступа: предупреждение всегда на виду.</summary>
public sealed partial class QrWindowViewModel(string profileName, Bitmap qr, IAppClipboard clipboard) : ObservableObject
{
    public string ProfileName { get; } = profileName;

    public Bitmap Qr { get; } = qr;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [RelayCommand]
    private async Task CopyImageAsync()
    {
        await clipboard.SetBitmapAsync(Qr).ConfigureAwait(true);
        Message = Localizer.Get("QrImageCopied");
    }
}

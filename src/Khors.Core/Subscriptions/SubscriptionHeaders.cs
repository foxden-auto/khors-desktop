using System.Globalization;
using System.Net.Http.Headers;
using Khors.Core.Text;

namespace Khors.Core.Subscriptions;

/// <summary>Разбор заголовков ответа подписки (общепринятые у панелей и клиентов).</summary>
public static class SubscriptionHeaders
{
    /// <summary><c>subscription-userinfo: upload=…; download=…; total=…; expire=…</c> (байты, unix-время в секундах).</summary>
    public static SubscriptionUserInfo? ParseUserInfo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        long? upload = null, download = null, total = null;
        DateTimeOffset? expire = null;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !long.TryParse(part[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < 0)
            {
                continue;
            }

            switch (part[..eq].Trim().ToLowerInvariant())
            {
                case "upload":
                    upload = number;
                    break;
                case "download":
                    download = number;
                    break;
                case "total":
                    total = number;
                    break;
                case "expire" when number > 0:
                    expire = DateTimeOffset.FromUnixTimeSeconds(Math.Min(number, 253402300799));
                    break;
            }
        }

        return upload is null && download is null && total is null && expire is null
            ? null
            : new SubscriptionUserInfo(upload, download, total, expire);
    }

    /// <summary><c>profile-update-interval</c> в часах (целое, 1…8760).</summary>
    public static int? ParseUpdateInterval(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) && hours is >= 1 and <= 8760
            ? hours
            : null;

    /// <summary>
    /// Имя подписки: <c>profile-title</c> (обычный текст или <c>base64:…</c>), затем имя файла из
    /// <c>content-disposition</c>; <c>null</c> — не указано.
    /// </summary>
    public static string? ParseTitle(string? profileTitle, string? contentDisposition)
    {
        if (!string.IsNullOrWhiteSpace(profileTitle))
        {
            var title = profileTitle.Trim();
            if (title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            {
                return Base64Text.TryDecodeUtf8(title[7..], out var decoded) && !string.IsNullOrWhiteSpace(decoded) ? decoded.Trim() : null;
            }

            return title;
        }

        if (!string.IsNullOrWhiteSpace(contentDisposition)
            && ContentDispositionHeaderValue.TryParse(contentDisposition, out var disposition))
        {
            var file = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"').Trim();
            if (!string.IsNullOrEmpty(file))
            {
                return Path.GetFileNameWithoutExtension(file);
            }
        }

        return null;
    }
}

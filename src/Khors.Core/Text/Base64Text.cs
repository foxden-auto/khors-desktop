using System.Text;

namespace Khors.Core.Text;

/// <summary>Декодирование base64 из ссылок и подписок: обычный и URL-safe алфавит, с паддингом и без.</summary>
internal static class Base64Text
{
    private static readonly UTF8Encoding s_strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Декодирует base64 в строку UTF-8. Пробелы и переводы строк игнорируются, %-кодирование снимается.</summary>
    public static bool TryDecodeUtf8(string value, out string decoded)
    {
        decoded = string.Empty;
        var normalized = new StringBuilder(value.Length + 3);
        foreach (var c in Uri.UnescapeDataString(value))
        {
            if (!char.IsWhiteSpace(c))
            {
                normalized.Append(c switch { '-' => '+', '_' => '/', _ => c });
            }
        }

        var text = normalized.ToString().TrimEnd('=');
        if (text.Length == 0)
        {
            return false;
        }

        text += new string('=', (4 - (text.Length % 4)) % 4);
        var buffer = new byte[text.Length];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            return false;
        }

        try
        {
            decoded = s_strictUtf8.GetString(buffer, 0, written);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

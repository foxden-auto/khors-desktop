using ZXing;
using ZXing.Common;
using ZXing.QrCode.Internal;
using ZXing.Multi.QrCode;

namespace Khors.Core.Qr;

/// <summary>Модули QR-кода: квадрат <see cref="Size"/>×<see cref="Size"/> без поля тишины.</summary>
public sealed class QrMatrix
{
    private readonly bool[,] _dark;

    internal QrMatrix(bool[,] dark) => _dark = dark;

    public int Size => _dark.GetLength(0);

    /// <summary>Тёмный ли модуль в столбце <paramref name="x"/>, строке <paramref name="y"/>.</summary>
    public bool this[int x, int y] => _dark[x, y];
}

/// <summary>
/// Распознавание и построение QR-кодов (ZXing.Net). Чистые функции без ОС: на вход — пиксели BGRA,
/// на выход — тексты кодов или матрица модулей; картинки читает и рисует UI. Тексты содержат секреты — в лог не пишутся.
/// </summary>
public static class QrCodes
{
    /// <summary>Все QR-коды на картинке (несколько — для снимка экрана), без повторов, в порядке нахождения.</summary>
    /// <param name="bgra">Пиксели BGRA, 4 байта на пиксель, строки подряд без выравнивания.</param>
    public static IReadOnlyList<string> Decode(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgra.Length < width * height * 4)
        {
            throw new ArgumentException("Pixel buffer is smaller than width × height × 4.", nameof(bgra));
        }

        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var found = DecodeAll(source);

        // Светлый код на тёмном фоне (тёмные темы) — после инверсии.
        return found.Count > 0 ? found : DecodeAll(source.invert());
    }

    /// <summary>
    /// Матрица QR-кода для текста (UTF-8). Уровень коррекции M, для длинных ссылок — L.
    /// <c>null</c> — текст не помещается даже в самый большой QR-код.
    /// </summary>
    public static QrMatrix? Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hints = new Dictionary<EncodeHintType, object> { [EncodeHintType.CHARACTER_SET] = "UTF-8" };
        foreach (var level in new[] { ErrorCorrectionLevel.M, ErrorCorrectionLevel.L })
        {
            try
            {
                var matrix = Encoder.encode(text, level, hints).Matrix;
                var dark = new bool[matrix.Width, matrix.Height];
                for (var y = 0; y < matrix.Height; y++)
                {
                    for (var x = 0; x < matrix.Width; x++)
                    {
                        dark[x, y] = matrix[x, y] == 1;
                    }
                }

                return new QrMatrix(dark);
            }
            catch (WriterException)
            {
                // Не поместилось — следующий уровень коррекции.
            }
        }

        return null;
    }

    private static List<string> DecodeAll(LuminanceSource source)
    {
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
            [DecodeHintType.POSSIBLE_FORMATS] = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
        };

        var results = new QRCodeMultiReader().decodeMultiple(new BinaryBitmap(new HybridBinarizer(source)), hints) ?? [];
        return [.. results.Select(r => r.Text).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.Ordinal)];
    }
}

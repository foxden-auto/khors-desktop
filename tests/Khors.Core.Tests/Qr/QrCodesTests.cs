using System.Text.Json.Nodes;
using Khors.Core.Qr;
using Xunit;

namespace Khors.Core.Tests.Qr;

/// <summary>Круг «текст → матрица → пиксели → текст». Ссылки — вымышленные.</summary>
public class QrCodesTests
{
    private const string Link = "vless://3f1c2a9e-7b4d-4e8a-9c21-5d6e7f809a1b@vpn.example.com:443?type=tcp&security=reality&sni=www.example.org&fp=chrome&pbk=Zx8Q2mT9vK4rL7pW1sN6bY3cH5jD0fG8aE2uI9oP4kM&sid=6ba85179e30d4fc2#%D0%93%D0%B5%D1%80%D0%BC%D0%B0%D0%BD%D0%B8%D1%8F";

    [Fact]
    public void EncodedLinkIsDecodedBack()
    {
        var image = Image.Of(QrCodes.Encode(Link)!);

        Assert.Equal([Link], QrCodes.Decode(image.Pixels, image.Width, image.Height));
    }

    [Fact]
    public void UnicodeTextSurvives()
    {
        const string text = "trojan://Fictional-Pa55@vpn.example.com:443#Германия 🇩🇪";
        var image = Image.Of(QrCodes.Encode(text)!);

        Assert.Equal([text], QrCodes.Decode(image.Pixels, image.Width, image.Height));
    }

    [Fact]
    public void LongPostQuantumLinkFitsAndIsDecoded()
    {
        // Ссылка с ключом ML-DSA-65 (pqv, ~2,6 КБ) не помещается с коррекцией M — берётся L.
        var link = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "links", "vless-reality-xhttp.json")))!["link"]!.GetValue<string>();
        var matrix = QrCodes.Encode(link);

        Assert.NotNull(matrix);
        var image = Image.Of(matrix, scale: 3);
        Assert.Equal([link], QrCodes.Decode(image.Pixels, image.Width, image.Height));
    }

    [Fact]
    public void SeveralCodesOnOneImageAreAllFound()
    {
        const string second = "hysteria2://Hy2Pa55@hy.example.com:443/?sni=hy.example.com#Hy2";
        var image = Image.SideBySide(Image.Of(QrCodes.Encode(Link)!), Image.Of(QrCodes.Encode(second)!));

        var found = QrCodes.Decode(image.Pixels, image.Width, image.Height);

        Assert.Equal(2, found.Count);
        Assert.Contains(Link, found);
        Assert.Contains(second, found);
    }

    [Fact]
    public void LightCodeOnDarkBackgroundIsFound()
    {
        var image = Image.Of(QrCodes.Encode(Link)!, inverted: true);

        Assert.Equal([Link], QrCodes.Decode(image.Pixels, image.Width, image.Height));
    }

    [Fact]
    public void ImageWithoutCodeGivesNothing()
    {
        var blank = new byte[200 * 100 * 4];
        Array.Fill(blank, (byte)0xFF);

        Assert.Empty(QrCodes.Decode(blank, 200, 100));
    }

    [Fact]
    public void TooLongTextCannotBeEncoded() => Assert.Null(QrCodes.Encode(new string('a', 5000)));

    /// <summary>Картинка BGRA: модуль — <c>scale</c>×<c>scale</c> пикселей, поле тишины — 4 модуля.</summary>
    private sealed record Image(byte[] Pixels, int Width, int Height)
    {
        public static Image Of(QrMatrix matrix, int scale = 4, bool inverted = false)
        {
            const int quiet = 4;
            var size = (matrix.Size + (2 * quiet)) * scale;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var mx = (x / scale) - quiet;
                    var my = (y / scale) - quiet;
                    var dark = mx >= 0 && my >= 0 && mx < matrix.Size && my < matrix.Size && matrix[mx, my];
                    var value = (byte)(dark ^ inverted ? 0x00 : 0xFF);
                    var at = ((y * size) + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                    pixels[at + 3] = 0xFF;
                }
            }

            return new Image(pixels, size, size);
        }

        public static Image SideBySide(Image left, Image right)
        {
            var width = left.Width + right.Width;
            var height = Math.Max(left.Height, right.Height);
            var pixels = new byte[width * height * 4];
            Array.Fill(pixels, (byte)0xFF);
            Copy(left, 0);
            Copy(right, left.Width);
            return new Image(pixels, width, height);

            void Copy(Image from, int offsetX)
            {
                for (var y = 0; y < from.Height; y++)
                {
                    Array.Copy(from.Pixels, y * from.Width * 4, pixels, ((y * width) + offsetX) * 4, from.Width * 4);
                }
            }
        }
    }
}

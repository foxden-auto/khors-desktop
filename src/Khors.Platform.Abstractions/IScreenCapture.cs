namespace Khors.Platform;

/// <summary>Снимок экрана: пиксели BGRA (4 байта на пиксель, строки подряд, альфа = 255).</summary>
public sealed record ScreenImage(byte[] Bgra, int Width, int Height);

/// <summary>Снимок всех мониторов — для поиска QR-кодов на экране (docs/SPEC.md, 4.2). Пиксели нигде не сохраняются.</summary>
public interface IScreenCapture
{
    /// <summary>Весь рабочий стол (все мониторы). Ошибка ОС — <see cref="InvalidOperationException"/>.</summary>
    ScreenImage CaptureAllScreens();
}

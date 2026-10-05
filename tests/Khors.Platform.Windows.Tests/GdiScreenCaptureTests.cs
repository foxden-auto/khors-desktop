using Khors.Platform.Windows.Screen;
using Xunit;

namespace Khors.Platform.Windows.Tests;

public class GdiScreenCaptureTests
{
    [Fact]
    public void CapturesWholeVirtualScreenWithOpaquePixels()
    {
        var image = new GdiScreenCapture().CaptureAllScreens();

        Assert.True(image.Width > 0 && image.Height > 0);
        Assert.Equal(image.Width * image.Height * 4, image.Bgra.Length);
        Assert.All(Enumerable.Range(0, image.Width * image.Height).Where(i => i % 997 == 0), i => Assert.Equal(0xFF, image.Bgra[(i * 4) + 3]));
    }
}

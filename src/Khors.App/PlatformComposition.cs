using Khors.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Khors.App;

/// <summary>Единственное место, где UI выбирает реализацию платформы (docs/SPEC.md, 3.5).</summary>
internal static class PlatformComposition
{
    public static IServiceCollection AddPlatform(this IServiceCollection services)
    {
        if (OperatingSystem.IsWindows())
        {
            return services.AddWindowsPlatform();
        }

        throw new PlatformNotSupportedException("KHORS Desktop supports only Windows for now.");
    }
}

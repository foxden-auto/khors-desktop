using Xunit;

namespace Khors.Platform.Windows.Tests;

/// <summary>Каталог данных: установленная версия, переносимая (признак рядом с exe) и переопределение для тестов.</summary>
public sealed class DataDirectoryTests : IDisposable
{
    private readonly string _app = Directory.CreateTempSubdirectory("khors-app-").FullName;

    public void Dispose() => Directory.Delete(_app, recursive: true);

    [Fact]
    public void InstalledUsesRoamingAppData() =>
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "KHORS"), WindowsPlatform.ResolveDataDirectory(null, _app, @"C:\Users\u\AppData\Roaming"));

    [Fact]
    public void PortableUsesDataNextToExe()
    {
        File.WriteAllText(Path.Combine(_app, WindowsPlatform.PortableMarker), "portable");

        Assert.Equal(Path.Combine(_app, "data"), WindowsPlatform.ResolveDataDirectory(null, _app, @"C:\Users\u\AppData\Roaming"));
    }

    [Fact]
    public void OverrideWinsOverPortable()
    {
        File.WriteAllText(Path.Combine(_app, WindowsPlatform.PortableMarker), "portable");
        var custom = Path.Combine(_app, "custom");

        Assert.Equal(custom, WindowsPlatform.ResolveDataDirectory(custom, _app, @"C:\Users\u\AppData\Roaming"));
    }
}

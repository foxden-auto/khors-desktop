using System.Runtime.InteropServices;

namespace Khors.Engines;

/// <summary>
/// Поиск исполняемого файла ядра. Порядок: путь, заданный пользователем (docs/SPEC.md, 4.8);
/// поставка — <c>&lt;каталог приложения&gt;/cores/&lt;ядро&gt;/</c>; разработка — <c>&lt;репозиторий&gt;/cores/&lt;rid&gt;/&lt;ядро&gt;/</c>
/// (туда кладёт ядра tools/cores/fetch-cores.cs).
/// </summary>
public static class CoreLocator
{
    public static string? Find(CoreKind kind, string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        var directory = DirectoryName(kind);
        var executable = ExecutableName(kind);

        var shipped = Path.Combine(AppContext.BaseDirectory, "cores", directory, executable);
        if (File.Exists(shipped))
        {
            return shipped;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Khors.slnx")))
            {
                var development = Path.Combine(dir.FullName, "cores", RuntimeInformation.RuntimeIdentifier, directory, executable);
                return File.Exists(development) ? development : null;
            }
        }

        return null;
    }

    private static string DirectoryName(CoreKind kind) => kind switch
    {
        CoreKind.Xray => "xray",
        CoreKind.SingBox => "sing-box",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ExecutableName(CoreKind kind) =>
        DirectoryName(kind) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
}

using System.Runtime.InteropServices;

namespace Khors.Engines;

/// <summary>
/// Поиск исполняемого файла ядра. Порядок: путь, заданный пользователем (docs/SPEC.md, 4.8); обновлённая версия
/// из хранилища (<see cref="Store"/>, ROADMAP 5.2); поставка — <c>&lt;каталог приложения&gt;/cores/&lt;ядро&gt;/</c>;
/// разработка — <c>&lt;репозиторий&gt;/cores/&lt;rid&gt;/&lt;ядро&gt;/</c> (туда кладёт ядра tools/cores/fetch-cores.cs).
/// </summary>
public static class CoreLocator
{
    /// <summary>
    /// Хранилище обновлённых ядер процесса: окно — в данных пользователя, служба — в своём каталоге (CLAUDE.md, правило 8);
    /// <c>null</c> — только поставка. Задаётся при запуске.
    /// </summary>
    public static Cores.CoreStore? Store { get; set; }

    public static string? Find(CoreKind kind, string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        if (Store?.ActiveExecutable(kind) is { } updated)
        {
            return updated;
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
        CoreKind.SingBoxExtended => "sing-box-extended",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static string ExecutableName(CoreKind kind) =>
        (kind == CoreKind.SingBoxExtended ? "sing-box" : DirectoryName(kind)) + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
}

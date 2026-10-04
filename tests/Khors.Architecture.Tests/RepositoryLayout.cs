namespace Khors.Architecture.Tests;

internal static class RepositoryLayout
{
    /// <summary>Библиотеки, которые не должны зависеть от реализаций конкретной ОС.</summary>
    public static readonly string[] OsIndependentLibraries =
    [
        "Khors.Core",
        "Khors.Engines",
        "Khors.Ipc",
        "Khors.Platform.Abstractions",
    ];

    /// <summary>Исполняемые точки сборки: ссылаются на Khors.Platform.&lt;ОС&gt; только из этого файла.</summary>
    public const string CompositionFileName = "PlatformComposition.cs";

    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    public static bool IsOsSpecificPlatformProject(string projectName) =>
        projectName.StartsWith("Khors.Platform.", StringComparison.Ordinal)
        && projectName != "Khors.Platform.Abstractions";

    public static IEnumerable<string> SourceFiles(string projectDirectory) =>
        Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(Path.GetRelativePath(projectDirectory, path)));

    private static bool IsBuildOutput(string relativePath)
    {
        var first = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Khors.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (Khors.slnx) not found.");
    }
}

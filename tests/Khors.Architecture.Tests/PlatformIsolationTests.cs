using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Khors.Architecture.Tests;

public partial class PlatformIsolationTests
{
    public static TheoryData<string> OsIndependentLibraries => new(RepositoryLayout.OsIndependentLibraries);

    [Theory]
    [MemberData(nameof(OsIndependentLibraries))]
    public void LibraryProjectDoesNotReferenceOsSpecificOrExecutableProjects(string project)
    {
        var references = ProjectReferences(project);

        Assert.DoesNotContain(references, r => RepositoryLayout.IsOsSpecificPlatformProject(r));
        Assert.DoesNotContain("Khors.App", references);
        Assert.DoesNotContain("Khors.Service", references);
    }

    [Theory]
    [MemberData(nameof(OsIndependentLibraries))]
    public void LibraryAssemblyDoesNotReferenceOsSpecificAssemblies(string project)
    {
        var references = Assembly.Load(project).GetReferencedAssemblies().Select(a => a.Name!);

        Assert.DoesNotContain(references, r => RepositoryLayout.IsOsSpecificPlatformProject(r));
    }

    /// <summary>Пакеты, которые Khors.Core может использовать: чистые библиотеки без ОС и UI (каждый — в NOTICE).</summary>
    private static readonly string[] s_coreAllowedPackages = ["YamlDotNet"];

    [Fact]
    public void CoreDependsOnlyOnBaseLibraryAndAllowedPackages()
    {
        Assert.Empty(ProjectReferences("Khors.Core"));
        Assert.All(ReferenceItems("Khors.Core", "PackageReference"), p => Assert.Contains(p, s_coreAllowedPackages));

        var references = Assembly.Load("Khors.Core").GetReferencedAssemblies().Select(a => a.Name!);
        Assert.All(references, r => Assert.True(
            r.StartsWith("System.", StringComparison.Ordinal) || r is "System" or "netstandard" || s_coreAllowedPackages.Contains(r),
            $"Khors.Core references {r}"));
    }

    [Fact]
    public void OsSpecificCodeExistsOnlyInPlatformProjects()
    {
        var violations = new List<string>();

        foreach (var projectDir in Directory.EnumerateDirectories(RepositoryLayout.Src))
        {
            var project = Path.GetFileName(projectDir);
            if (RepositoryLayout.IsOsSpecificPlatformProject(project))
            {
                continue;
            }

            foreach (var file in RepositoryLayout.SourceFiles(projectDir))
            {
                var text = File.ReadAllText(file);
                var relative = Path.GetRelativePath(RepositoryLayout.Root, file);

                foreach (Match match in OsSpecificApi().Matches(text))
                {
                    violations.Add($"{relative}: {match.Value}");
                }

                var isComposition = Path.GetFileName(file) == RepositoryLayout.CompositionFileName
                    && project is "Khors.App" or "Khors.Service";
                if (!isComposition)
                {
                    foreach (Match match in OsSpecificPlatformNamespace().Matches(text))
                    {
                        violations.Add($"{relative}: {match.Value} outside {RepositoryLayout.CompositionFileName}");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static string[] ProjectReferences(string project) =>
        ReferenceItems(project, "ProjectReference")
            .Select(include => Path.GetFileNameWithoutExtension(include.Replace('\\', '/')))
            .ToArray();

    private static string[] ReferenceItems(string project, string itemName)
    {
        var csproj = Path.Combine(RepositoryLayout.Src, project, project + ".csproj");
        return XDocument.Load(csproj)
            .Descendants(itemName)
            .Select(e => (string?)e.Attribute("Include") ?? string.Empty)
            .ToArray();
    }

    [GeneratedRegex(@"\b(DllImport|LibraryImport|Microsoft\.Win32|System\.IO\.Pipes|System\.Management|Windows\.Win32)\b")]
    private static partial Regex OsSpecificApi();

    [GeneratedRegex(@"\bKhors\.Platform\.(?!Abstractions\b)[A-Z]\w*")]
    private static partial Regex OsSpecificPlatformNamespace();
}

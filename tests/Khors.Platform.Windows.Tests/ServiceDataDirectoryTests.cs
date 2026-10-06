using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Khors.Platform.Windows.Service;
using Xunit;

namespace Khors.Platform.Windows.Tests;

/// <summary>Каталог данных службы — на временном каталоге, без прав администратора (владелец — текущий пользователь).</summary>
public sealed class ServiceDataDirectoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "khors-service-data-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        // Каталог закрыт на запись для пользователей; владелец может вернуть себе права и удалить.
        foreach (var path in Directory.Exists(_root) ? Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root).ToList() : [])
        {
            var info = new DirectoryInfo(path);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                var security = new DirectorySecurity();
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                info.SetAccessControl(security);
            }
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreatesDirectoriesWritableOnlyBySystemAndAdministrators()
    {
        var path = Path.Combine(_root, "KHORS");

        ServiceDataDirectory.Ensure(path, "geo");

        foreach (var directory in new[] { path, Path.Combine(path, "geo") })
        {
            var security = new DirectoryInfo(directory).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
            Assert.Equal(FileSystemRights.FullControl, Rights(rules, WellKnownSidType.LocalSystemSid));
            Assert.Equal(FileSystemRights.FullControl, Rights(rules, WellKnownSidType.BuiltinAdministratorsSid));
            Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, Rights(rules, WellKnownSidType.BuiltinUsersSid) | FileSystemRights.Synchronize);
            // Плюс учётная запись процесса (у службы — SYSTEM, здесь — пользователь теста); других записей нет.
            var current = WindowsIdentity.GetCurrent().User!;
            Assert.Equal(FileSystemRights.FullControl, rules.Where(r => r.IdentityReference == current).Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights));
            Assert.Equal(4, rules.Select(r => r.IdentityReference).Distinct().Count());
        }
    }

    [Fact]
    public void RepeatedEnsureKeepsFiles()
    {
        var path = Path.Combine(_root, "KHORS");
        ServiceDataDirectory.Ensure(path, "geo");
        File.WriteAllText(Path.Combine(path, "geo", "geoip.dat"), "data");

        ServiceDataDirectory.Ensure(path, "geo");

        Assert.Equal("data", File.ReadAllText(Path.Combine(path, "geo", "geoip.dat")));
    }

    [Fact]
    public void JunctionIsReplacedWithoutTouchingItsTarget()
    {
        var target = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "important.txt"), "keep");
        var path = Path.Combine(_root, "KHORS");
        Directory.CreateDirectory(path);
        var geo = Path.Combine(path, "geo");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", geo, target]) { CreateNoWindow = true, UseShellExecute = false })!)
        {
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }

        ServiceDataDirectory.Ensure(path, "geo");

        Assert.False(new DirectoryInfo(geo).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Empty(Directory.EnumerateFileSystemEntries(geo));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "important.txt")));
    }

    private static FileSystemRights Rights(IEnumerable<FileSystemAccessRule> rules, WellKnownSidType sid) =>
        rules.Where(r => ((SecurityIdentifier)r.IdentityReference).IsWellKnown(sid)).Aggregate((FileSystemRights)0, (all, r) => all | r.FileSystemRights);
}

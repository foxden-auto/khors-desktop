using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Khors.Platform.Windows.Service;

/// <summary>
/// Каталог данных службы <c>%ProgramData%\KHORS</c>. В <c>%ProgramData%</c> обычный пользователь может создавать
/// каталоги, поэтому служба (SYSTEM) не доверяет найденному: каталог, созданный не службой и не администратором,
/// или ссылка (junction) удаляются и создаются заново с закрытыми правами — пишут SYSTEM, администраторы и сама
/// служба, пользователи только читают. Иначе пользователь мог бы подложить файлы, которые служба передаст ядрам.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ServiceDataDirectory
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "KHORS");

    /// <summary>Создаёт или исправляет каталог и его подкаталоги <paramref name="children"/>.</summary>
    public static void Ensure(string path, params string[] children)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        EnsureOne(path);
        foreach (var child in children)
        {
            EnsureOne(Path.Combine(path, child));
        }
    }

    private static void EnsureOne(string path)
    {
        var directory = new DirectoryInfo(path);
        if (directory.Exists && (directory.Attributes.HasFlag(FileAttributes.ReparsePoint) || !HasTrustedOwner(directory)))
        {
            // Для ссылки удаляется сама ссылка, а не то, на что она указывает.
            directory.Delete(recursive: !directory.Attributes.HasFlag(FileAttributes.ReparsePoint));
            directory.Refresh();
        }

        if (directory.Exists)
        {
            directory.SetAccessControl(Security());
        }
        else
        {
            directory.Create(Security());
        }
    }

    /// <summary>Владелец — SYSTEM, администраторы или учётная запись этого процесса (сама служба; в тестах — пользователь).</summary>
    private static bool HasTrustedOwner(DirectoryInfo directory)
    {
        var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        using var current = WindowsIdentity.GetCurrent();
        return owner is SecurityIdentifier sid
            && (sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
                || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || sid == current.User);
    }

    private static DirectorySecurity Security()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));

        // Учётная запись процесса: у службы это SYSTEM (правило выше), в тестах — пользователь, создающий каталог.
        using var current = WindowsIdentity.GetCurrent();
        if (current.User is { } user && !user.IsWellKnown(WellKnownSidType.LocalSystemSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        return security;
    }
}

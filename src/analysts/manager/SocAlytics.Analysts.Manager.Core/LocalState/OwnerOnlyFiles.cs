using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SocAlytics.Analysts.Manager.LocalState;

public static class OwnerOnlyFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            ProtectDirectoryWindows(directory);
            return;
        }

        Directory.CreateDirectory(directory, DirectoryMode);
        File.SetUnixFileMode(directory, DirectoryMode);
    }

    /// <summary>Writes through a temporary owner-only file and replaces the target atomically.</summary>
    public static void WriteAtomic(string path, byte[] content)
    {
        var directory = Path.GetDirectoryName(path)!;
        EnsureDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = FileMode;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content);
                stream.Flush(true);
            }

            if (OperatingSystem.IsWindows())
            {
                ProtectFileWindows(temp);
            }

            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public static bool IsOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return IsOwnerOnlyWindows(path);
        }

        return (File.GetUnixFileMode(path) & ~FileMode) == 0
            && (File.GetUnixFileMode(Path.GetDirectoryName(path)!) & ~DirectoryMode) == 0;
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectoryWindows(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFileWindows(string path)
    {
        var info = new FileInfo(path);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyWindows(string path)
    {
        var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
        var me = WindowsIdentity.GetCurrent().User;
        return rules.Cast<FileSystemAccessRule>()
            .Where(r => r.AccessControlType == AccessControlType.Allow)
            .All(r => r.IdentityReference.Equals(me));
    }
}

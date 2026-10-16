using System.Runtime.InteropServices;

namespace SocAlytics.Analysts.Manager.Runtime;

/// <summary>The login shell of the account the Manager runs under; the source is injectable for tests.</summary>
public sealed class AccountShell(Func<string?>? source = null)
{
    private static readonly string[] NoLoginShells = ["/usr/sbin/nologin", "/sbin/nologin", "/bin/false", "/usr/bin/false"];

    private readonly Func<string?> source = source ?? ReadCurrent;

    /// <summary>The login shell, or null when it cannot be determined.</summary>
    public string? LoginShell()
    {
        try
        {
            return source();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>An unknown shell counts as login-capable so the account rule fails closed.</summary>
    public bool IsLoginCapable() => !IsNoLogin(LoginShell());

    public static bool IsNoLogin(string? shell) =>
        !string.IsNullOrWhiteSpace(shell) && NoLoginShells.Contains(shell.Trim(), StringComparer.Ordinal);

    /// <summary>Reads the shell field of the <c>/etc/passwd</c> line for the user id; null when there is no such line.</summary>
    public static string? ParsePasswd(IEnumerable<string> lines, uint uid, string? userName)
    {
        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length < 7 || line.StartsWith('#'))
            {
                continue;
            }

            if (uint.TryParse(fields[2], out var id) && id == uid)
            {
                return fields[6].Length == 0 ? "/bin/sh" : fields[6];
            }
        }

        if (userName is null)
        {
            return null;
        }

        foreach (var line in lines)
        {
            var fields = line.Split(':');
            if (fields.Length >= 7 && !line.StartsWith('#') && fields[0] == userName)
            {
                return fields[6].Length == 0 ? "/bin/sh" : fields[6];
            }
        }

        return null;
    }

    private static string? ReadCurrent()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return null;
        }

        var uid = geteuid();
        if (OperatingSystem.IsLinux())
        {
            var shell = FromGetpwuidR(uid);
            if (shell is not null)
            {
                return shell;
            }
        }

        return File.Exists("/etc/passwd") ? ParsePasswd(File.ReadLines("/etc/passwd"), uid, Environment.UserName) : null;
    }

    private static string? FromGetpwuidR(uint uid)
    {
        var buffer = Marshal.AllocHGlobal(16384);
        try
        {
            var passwd = Marshal.AllocHGlobal(Marshal.SizeOf<Passwd>());
            try
            {
                if (getpwuid_r(uid, passwd, buffer, (nuint)16384, out var result) != 0 || result == IntPtr.Zero)
                {
                    return null;
                }

                var entry = Marshal.PtrToStructure<Passwd>(passwd);
                return Marshal.PtrToStringUTF8(entry.Shell);
            }
            finally
            {
                Marshal.FreeHGlobal(passwd);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // glibc and musl layout of struct passwd.
    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name;
        public IntPtr Password;
        public uint Uid;
        public uint Gid;
        public IntPtr Gecos;
        public IntPtr Dir;
        public IntPtr Shell;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "getpwuid_r")]
    private static extern int getpwuid_r(uint uid, IntPtr pwd, IntPtr buf, nuint buflen, out IntPtr result);
}

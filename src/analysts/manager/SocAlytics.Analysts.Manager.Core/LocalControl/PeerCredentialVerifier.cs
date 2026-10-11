using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SocAlytics.Analysts.Manager.LocalControl;

/// <summary>Accepts a connection only when the peer runs as the owning user; the uid readers are injectable for tests.</summary>
public sealed class PeerCredentialVerifier(Func<Socket, uint?>? readPeerUid = null, uint? ownerUid = null)
{
    public bool IsOwner(Socket connection)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return IsOwnerWindows(connection);
            }

            var peer = (readPeerUid ?? ReadPeerUid)(connection);
            return peer is { } uid && uid == (ownerUid ?? geteuid());
        }
        catch (Exception e) when (e is SocketException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static uint? ReadPeerUid(Socket socket)
    {
        if (OperatingSystem.IsLinux())
        {
            // struct ucred { pid_t pid; uid_t uid; gid_t gid; }; SOL_SOCKET = 1, SO_PEERCRED = 17.
            var buffer = new byte[12];
            return socket.GetRawSocketOption(1, 17, buffer) == 12 ? BitConverter.ToUInt32(buffer, 4) : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            return getpeereid((int)socket.Handle, out var uid, out _) == 0 ? uid : null;
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerWindows(Socket socket)
    {
        // SIO_AF_UNIX_GETPEERPID
        var output = new byte[4];
        socket.IOControl(unchecked((int)0x58000100), null, output);
        var pid = BitConverter.ToUInt32(output, 0);
        var process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, 0x8, out var token))
            {
                return false;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User is { } user && WindowsIdentity.GetCurrent().User is { } me && user.Equals(me);
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    private static extern int getpeereid(int socket, out uint uid, out uint gid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

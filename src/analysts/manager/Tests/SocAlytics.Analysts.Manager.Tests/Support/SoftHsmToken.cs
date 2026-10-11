using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Support;

[CollectionDefinition(Name)]
public sealed class SoftHsmCollection : ICollectionFixture<SoftHsmToken>
{
    public const string Name = "softhsm";
}

[SupportedOSPlatform("linux")]
public sealed class SoftHsmToken : IDisposable
{
    public const string ModulePath = "/usr/lib/softhsm/libsofthsm2.so";
    public const string TokenLabel = "socalytics-device";

    public SoftHsmToken()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!File.Exists(ModulePath))
        {
            throw new FileNotFoundException("SoftHSM2 module is required on Linux.", ModulePath);
        }

        Root = Directory.CreateTempSubdirectory("socalytics-softhsm-").FullName;
        var tokens = Path.Combine(Root, "tokens");
        Directory.CreateDirectory(tokens);
        var conf = Path.Combine(Root, "softhsm2.conf");
        File.WriteAllText(conf, $"directories.tokendir = {tokens}\nobjectstore.backend = file\nlog.level = ERROR\n");

        if (setenv("SOFTHSM2_CONF", conf, 1) != 0)
        {
            throw new InvalidOperationException("setenv failed.");
        }

        UserPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var soPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var psi = new ProcessStartInfo("softhsm2-util")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["SOFTHSM2_CONF"] = conf;
        foreach (var a in new[] { "--init-token", "--free", "--label", TokenLabel, "--so-pin", soPin, "--pin", UserPin })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("softhsm2-util failed: " + process.StandardError.ReadToEnd());
        }
    }

    public string Root { get; } = string.Empty;

    public string UserPin { get; } = string.Empty;

    /// <summary>Creates a state directory with an owner-only pkcs11.pin, mirroring operator provisioning.</summary>
    public string NewStateDirectory(string? pin = null, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite)
    {
        var dir = Directory.CreateTempSubdirectory("socalytics-state-").FullName;
        var file = Path.Combine(dir, "pkcs11.pin");
        File.WriteAllText(file, pin ?? UserPin);
        File.SetUnixFileMode(file, mode);
        return dir;
    }

    public void Dispose()
    {
        if (Root.Length > 0)
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>A throwaway SoftHSM2 token for the Manager's PKCS#11 key provider; <see cref="TryCreate"/> returns null when SoftHSM2 is missing.</summary>
internal sealed class SoftHsmToken : IDisposable
{
    public const string TokenLabel = "socalytics-device";

    private static readonly string[] ModuleCandidates =
    [
        "/usr/lib/softhsm/libsofthsm2.so",
        "/usr/lib/x86_64-linux-gnu/softhsm/libsofthsm2.so",
        "/usr/lib/aarch64-linux-gnu/softhsm/libsofthsm2.so",
    ];

    private SoftHsmToken(string modulePath, string root, string userPin)
    {
        ModulePath = modulePath;
        Root = root;
        UserPin = userPin;
    }

    public string ModulePath { get; }

    public string Root { get; }

    public string UserPin { get; }

    public static SoftHsmToken? TryCreate()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var module = ModuleCandidates.FirstOrDefault(File.Exists);
        if (module is null)
        {
            return null;
        }

        var root = Directory.CreateTempSubdirectory("socalytics-e2e-softhsm-").FullName;
        var tokens = Path.Combine(root, "tokens");
        Directory.CreateDirectory(tokens);
        var conf = Path.Combine(root, "softhsm2.conf");
        File.WriteAllText(conf, $"directories.tokendir = {tokens}\nobjectstore.backend = file\nlog.level = ERROR\n");
        if (setenv("SOFTHSM2_CONF", conf, 1) != 0)
        {
            throw new InvalidOperationException("setenv failed.");
        }

        var userPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var soPin = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var psi = new ProcessStartInfo("softhsm2-util") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["SOFTHSM2_CONF"] = conf;
        foreach (var argument in new[] { "--init-token", "--free", "--label", TokenLabel, "--so-pin", soPin, "--pin", userPin })
        {
            psi.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(psi)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("softhsm2-util failed: " + error);
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Directory.Delete(root, true);
            return null;
        }

        return new SoftHsmToken(module, root, userPin);
    }

    /// <summary>Creates a Manager state directory with an owner-only pkcs11.pin, mirroring operator provisioning.</summary>
    public string NewStateDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("socalytics-e2e-state-").FullName;
        var file = Path.Combine(directory, "pkcs11.pin");
        File.WriteAllText(file, UserPin);
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return directory;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }

    [DllImport("libc", EntryPoint = "setenv", CharSet = CharSet.Ansi)]
    private static extern int setenv(string name, string value, int overwrite);
}

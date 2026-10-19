using System.Net;
using System.Net.Sockets;
using SocAlytics.Platform.Images.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Compose.Tests.Support;

/// <summary>
/// A disposable stamp: a directory created by the setup console under the system temporary directory, operator values filled with
/// disposable values, a unique Compose project, and teardown that removes the project's containers, networks, and volumes.
/// </summary>
public sealed class StampFixture(string? overrideFile = null) : IAsyncLifetime
{
    // The images are built once per run and shared by every stamp; they are removed when the test process ends.
    private static readonly Lazy<(ImageBuildFixture Fixture, Task<BuiltImages> Images)> SharedImages = new(() =>
    {
        var fixture = new ImageBuildFixture();
        fixture.InitializeAsync().AsTask().GetAwaiter().GetResult();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return (fixture, fixture.BuildAllAsync());
    });

    private bool _started;

    public string ProjectName { get; } = "socalytics-stamp-" + Guid.NewGuid().ToString("N")[..12];

    public string StampDirectory { get; } = Path.Combine(Path.GetTempPath(), "socalytics-stamp-" + Guid.NewGuid().ToString("N")[..12]);

    public string EnvFile => Path.Combine(StampDirectory, "stamp.env");

    public ComposeCli Cli { get; private set; } = null!;

    public BuiltImages Images { get; private set; } = null!;

    public int WebPort { get; private set; }

    public string WebOrigin => $"http://127.0.0.1:{WebPort}";

    public async ValueTask InitializeAsync()
    {
        await ComposeCli.EnsureAvailableAsync();
        Images = await SharedImages.Value.Images;

        var exitCode = StampSetup.Program.Run(["--output", StampDirectory], TextWriter.Null, TextWriter.Null);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"The setup console failed with exit code {exitCode} for '{StampDirectory}'.");
        }

        WebPort = FreeLoopbackPort();
        Set("SOCALYTICS_API_IMAGE", Images.ApiTag);
        Set("SOCALYTICS_MIGRATOR_IMAGE", Images.MigratorTag);
        Set("SOCALYTICS_WEB_IMAGE", Images.WebTag);
        Set("SOCALYTICS_RELEASE_VERSION", Images.Version);
        Set("SOCALYTICS_WEB_PORT", WebPort.ToString());
        Set("SOCALYTICS_STAMP_ID", "stamp-" + ProjectName[^12..]);
        Set("SOCALYTICS_PUBLIC_BASE_URI", "https://stamp.invalid");
        Set("SOCALYTICS_CLUB_DISPLAY_NAME", "Evidence Club " + ProjectName[^6..]);
        Set("SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME", "club.admin");
        Set("SOCALYTICS_SMTP_HOST", "smtp.invalid");
        Set("SOCALYTICS_SMTP_PORT", "587");
        Set("SOCALYTICS_SMTP_SECURITY", "StartTls");
        Set("SOCALYTICS_SENDER_ADDRESS", "noreply@stamp.invalid");
        Set("SOCALYTICS_SENDER_NAME", "SocAlytics Stamp");

        Cli = new ComposeCli(ProjectName, EnvFile, overrideFile);
    }

    /// <summary>Sets <paramref name="name"/> in the stamp file, replacing its current value or appending the assignment.</summary>
    public void Set(string name, string value)
    {
        var lines = File.ReadAllLines(EnvFile).ToList();
        var index = lines.FindIndex(line => line.StartsWith(name + "=", StringComparison.Ordinal));
        if (index >= 0)
        {
            lines[index] = $"{name}={value}";
        }
        else
        {
            lines.Add($"{name}={value}");
        }

        File.WriteAllLines(EnvFile, lines);
    }

    /// <summary>Returns the value of <paramref name="name"/> in the stamp file, or an empty string.</summary>
    public string Get(string name) =>
        File.ReadAllLines(EnvFile).Where(line => line.StartsWith(name + "=", StringComparison.Ordinal)).Select(line => line[(name.Length + 1)..]).FirstOrDefault() ?? string.Empty;

    /// <summary>Runs <c>up -d</c> and fails with the Compose output when it does not succeed.</summary>
    public async Task UpAsync()
    {
        _started = true;
        var (exitCode, output) = await Cli.RunAsync("up", "-d");
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"docker compose up -d failed with exit code {exitCode}:\n{output}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Cli is not null && (_started || Directory.Exists(StampDirectory)))
        {
            await Cli.RunAsync("down", "--volumes", "--remove-orphans");
        }

        if (Directory.Exists(StampDirectory))
        {
            Directory.Delete(StampDirectory, recursive: true);
        }
    }

    private static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}

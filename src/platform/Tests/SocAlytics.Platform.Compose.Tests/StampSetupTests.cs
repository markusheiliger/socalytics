using NATS.NKeys;
using Shouldly;
using Xunit;
using SetupProgram = SocAlytics.Platform.StampSetup.Program;

namespace SocAlytics.Platform.Compose.Tests;

public sealed class StampSetupTests : IDisposable
{
    private static readonly string[] SecretVariables =
    [
        "SOCALYTICS_FIRST_CLUB_ADMIN_INITIAL_PASSWORD",
        "SOCALYTICS_POSTGRES_PASSWORD",
        "SOCALYTICS_MIGRATOR_DB_PASSWORD",
        "SOCALYTICS_APP_DB_PASSWORD",
        "SOCALYTICS_STORAGE_ACCESS_KEY",
        "SOCALYTICS_STORAGE_SECRET_KEY",
    ];

    private static readonly string[] OperatorVariables =
    [
        "SOCALYTICS_API_IMAGE",
        "SOCALYTICS_MIGRATOR_IMAGE",
        "SOCALYTICS_WEB_IMAGE",
        "SOCALYTICS_RELEASE_VERSION",
        "SOCALYTICS_WEB_PORT",
        "SOCALYTICS_STAMP_ID",
        "SOCALYTICS_PUBLIC_BASE_URI",
        "SOCALYTICS_CLUB_DISPLAY_NAME",
        "SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME",
        "SOCALYTICS_SMTP_HOST",
        "SOCALYTICS_SMTP_PORT",
        "SOCALYTICS_SMTP_SECURITY",
        "SOCALYTICS_SENDER_ADDRESS",
        "SOCALYTICS_SENDER_NAME",
    ];

    private readonly List<string> _directories = [];

    [Fact]
    public void Setup_creates_the_stamp_file_and_nats_configuration()
    {
        var directory = NewDirectoryOutsideRepository();

        var exitCode = Run(directory, out _, out _);

        exitCode.ShouldBe(0);
        var values = ReadEnvironment(Path.Combine(directory, "stamp.env"));
        File.Exists(Path.Combine(directory, "nats.conf")).ShouldBeTrue();

        foreach (var name in SecretVariables)
        {
            values[name].Length.ShouldBe(32, name);
            values[name].ShouldAllBe(c => char.IsAsciiLetterOrDigit(c), name);
        }

        values["SOCALYTICS_STAMP_DIR"].ShouldBe(Path.GetFullPath(directory));
        Path.IsPathRooted(values["SOCALYTICS_STAMP_DIR"]).ShouldBeTrue();

        foreach (var name in OperatorVariables)
        {
            values[name].ShouldBeEmpty(name);
        }
    }

    [Fact]
    public void Setup_generates_distinct_secrets()
    {
        var directory = NewDirectoryOutsideRepository();

        Run(directory, out _, out _).ShouldBe(0);

        var values = ReadEnvironment(Path.Combine(directory, "stamp.env"));
        SecretVariables.Select(name => values[name]).Distinct().Count().ShouldBe(SecretVariables.Length);
    }

    [Fact]
    public void Setup_writes_nats_seeds_whose_public_keys_appear_in_the_server_configuration()
    {
        var directory = NewDirectoryOutsideRepository();

        Run(directory, out _, out _).ShouldBe(0);

        var values = ReadEnvironment(Path.Combine(directory, "stamp.env"));
        var config = File.ReadAllText(Path.Combine(directory, "nats.conf"));

        var operatorPair = KeyPair.FromSeed(values["SOCALYTICS_NATS_OPERATOR_SEED"]);
        var accountPair = KeyPair.FromSeed(values["SOCALYTICS_NATS_ACCOUNT_SEED"]);
        var systemPair = KeyPair.FromSeed(values["SOCALYTICS_NATS_SYSTEM_ACCOUNT_SEED"]);

        operatorPair.Prefix.ShouldBe(PrefixByte.Operator);
        accountPair.Prefix.ShouldBe(PrefixByte.Account);
        systemPair.Prefix.ShouldBe(PrefixByte.Account);

        new[] { operatorPair, accountPair, systemPair }.Select(pair => pair.GetPublicKey()).Distinct().Count().ShouldBe(3);
        config.ShouldContain($"system_account: {systemPair.GetPublicKey()}");
        config.ShouldContain($"{accountPair.GetPublicKey()}:");
        config.ShouldContain($"{systemPair.GetPublicKey()}:");
        config.ShouldContain("operator: ");
    }

    [Fact]
    public void Second_run_against_the_same_directory_fails_and_changes_nothing()
    {
        var directory = NewDirectoryOutsideRepository();
        Run(directory, out _, out _).ShouldBe(0);
        var before = Snapshot(directory);

        var exitCode = Run(directory, out _, out var error);

        exitCode.ShouldNotBe(0);
        error.ShouldNotBeNullOrWhiteSpace();
        Snapshot(directory).ShouldBe(before);
    }

    [Fact]
    public void Run_inside_the_repository_checkout_fails_and_creates_nothing()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "socalytics-stamp-" + Guid.NewGuid().ToString("N")[..12]);
        _directories.Add(directory);

        var exitCode = Run(directory, out _, out var error);

        exitCode.ShouldNotBe(0);
        error.ShouldNotBeNullOrWhiteSpace();
        Directory.Exists(directory).ShouldBeFalse();
        File.Exists(directory).ShouldBeFalse();
    }

    [Fact]
    public void Stamp_file_is_readable_by_its_owner_only_on_unix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = NewDirectoryOutsideRepository();

        Run(directory, out _, out _).ShouldBe(0);

        File.GetUnixFileMode(Path.Combine(directory, "stamp.env")).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Where(Directory.Exists))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static int Run(string directory, out string output, out string error)
    {
        using var outputWriter = new StringWriter();
        using var errorWriter = new StringWriter();
        var exitCode = SetupProgram.Run(["--output", directory], outputWriter, errorWriter);
        output = outputWriter.ToString();
        error = errorWriter.ToString();
        return exitCode;
    }

    private string NewDirectoryOutsideRepository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "socalytics-stamp-" + Guid.NewGuid().ToString("N")[..12]);
        _directories.Add(directory);
        return directory;
    }

    private static Dictionary<string, string> ReadEnvironment(string path) =>
        File.ReadAllLines(path)
            .Where(line => !line.StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1]);

    private static Dictionary<string, string> Snapshot(string directory) =>
        Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllText(path) : string.Empty);

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var git = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException("The repository checkout was not found above the test output directory.");
    }
}

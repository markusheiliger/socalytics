using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Images.Tests.Support;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Images.Tests;

[Collection(ImageBuildCollection.Name)]
public sealed class MigratorImageTests(ImageBuildFixture images) : IAsyncLifetime
{
    private const string Dockerfile = "src/platform/SocAlytics.Platform.Migrator/Dockerfile";
    private const string ConnectionStringKey = "ConnectionStrings__socalytics-migrator";

    private readonly ContainerProbe _probe = new();
    private INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private string _migratorPassword = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _migratorPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var appPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var initScript = Path.Combine(AppContext.BaseDirectory, "PostgresInit", "01-socalytics-roles.sh");

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync(ct);

        _postgres = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("socalytics")
            .WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", _migratorPassword)
            .WithEnvironment("SOCALYTICS_APP_PASSWORD", appPassword)
            .WithResourceMapping(new FileInfo(initScript), "/docker-entrypoint-initdb.d/")
            .WithNetwork(_network)
            .WithNetworkAliases("postgres")
            .Build();
        await _postgres.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        _probe.Dispose();
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DisposeAsync();
        }
    }

    [Fact]
    public async Task Migrator_applies_the_migrations_once_and_a_second_run_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await images.BuildAsync(Dockerfile);

        var first = await _probe.RunToCompletionAsync(image, _network!.Name, Environment(Connection("postgres", 5432)), ct: ct);

        first.ExitCode.ShouldBe(0, first.Logs);
        first.ReadonlyRootfs.ShouldBeTrue();
        (await _probe.IsRunningAsync(first.Id, ct)).ShouldBeFalse();
        var journal = await ReadJournalAsync(ct);
        journal.Count.ShouldBe(MigrationCatalog.Platform.Scripts.Count);
        journal.ShouldBe(MigrationCatalog.Platform.Scripts.Select(s => s.Identity).Order(StringComparer.Ordinal));

        var second = await _probe.RunToCompletionAsync(image, _network.Name, Environment(Connection("postgres", 5432)), ct: ct);

        second.ExitCode.ShouldBe(0, second.Logs);
        (await ReadJournalAsync(ct)).ShouldBe(journal);
    }

    [Fact]
    public async Task Migrator_without_a_connection_string_exits_with_the_configuration_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await images.BuildAsync(Dockerfile);

        var run = await _probe.RunToCompletionAsync(image, _network!.Name, ct: ct);

        run.ExitCode.ShouldBe(2, run.Logs);
    }

    [Fact]
    public async Task Migrator_against_an_unreachable_database_exits_with_the_database_unavailable_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await images.BuildAsync(Dockerfile);
        var environment = Environment(Connection("postgres", 1));
        environment["Migrator__ConnectTimeout"] = "00:00:03";

        var run = await _probe.RunToCompletionAsync(image, _network!.Name, environment, ct: ct);

        run.ExitCode.ShouldBe(3, run.Logs);
    }

    [Fact]
    public async Task Migrator_runs_as_the_unprivileged_user_and_the_image_holds_only_the_migrator()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await images.BuildAsync(Dockerfile);

        // An unreachable address with a long connect timeout keeps the process alive long enough to inspect it.
        var environment = Environment(Connection("192.0.2.1", 5432));
        environment["Migrator__ConnectTimeout"] = "00:02:00";
        var id = await _probe.CreateAsync(image, _network!.Name, environment, ct: ct);
        await _probe.StartAsync(id, ct);

        IReadOnlyList<string> uids = [];
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (uids.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct);
            uids = await _probe.ProcessUidsAsync(id, ct);
        }

        uids.ShouldNotBeEmpty();
        uids.ShouldAllBe(uid => uid == "1654");
        (await _probe.InspectContainerAsync(id, ct)).GetProperty("HostConfig").GetProperty("ReadonlyRootfs").GetBoolean().ShouldBeTrue();

        var files = await _probe.ListImageFilesAsync(image, ct);
        files.ShouldContain("/app/SocAlytics.Platform.Migrator.dll");
        files.ShouldNotContain("/app/SocAlytics.Platform.Api.dll");
    }

    [Fact]
    public async Task Migrator_with_an_arbitrary_non_root_user_still_starts_and_reports_the_configuration_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = await images.BuildAsync(Dockerfile);

        var run = await _probe.RunToCompletionAsync(image, _network!.Name, user: "12345:0", ct: ct);

        run.ExitCode.ShouldBe(2, run.Logs);
    }

    private string Connection(string host, int port) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = "socalytics",
            Username = "socalytics_migrator",
            Password = _migratorPassword,
            Pooling = false,
        }.ConnectionString;

    private static Dictionary<string, string> Environment(string connectionString) =>
        new() { [ConnectionStringKey] = connectionString };

    private async Task<IReadOnlyList<string>> ReadJournalAsync(CancellationToken ct)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_postgres!.GetConnectionString())
        {
            Database = "socalytics",
            Username = "socalytics_migrator",
            Password = _migratorPassword,
            Pooling = false,
        }.ConnectionString;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT identity FROM socalytics_migrations.history ORDER BY identity COLLATE \"C\"", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var identities = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            identities.Add(reader.GetString(0));
        }

        return identities;
    }
}

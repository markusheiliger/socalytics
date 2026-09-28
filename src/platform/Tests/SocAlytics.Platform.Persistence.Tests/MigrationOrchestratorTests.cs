using System.Text;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationOrchestratorTests
{
    [Fact]
    public async Task CleanDatabaseAppliesInModuleThenSequenceOrder()
    {
        await using var database = await StartDatabase();
        var scripts = new[]
        {
            Descriptor(ModuleKey.IdentityAccess, 1, "identity", "INSERT INTO public.migration_order VALUES ('identity');"),
            Descriptor(ModuleKey.Club, 2, "second", "INSERT INTO public.migration_order VALUES ('second');"),
            Descriptor(ModuleKey.Club, 1, "first", """
                CREATE TABLE public.migration_order (name text NOT NULL);
                INSERT INTO public.migration_order VALUES ('first');
                """)
        };

        Orchestrator(database.GetConnectionString(), scripts).Run();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        var order = (await connection.QueryAsync<string>("SELECT name FROM public.migration_order ORDER BY ctid")).ToArray();
        order.ShouldBe(["first", "second", "identity"]);

        var history = (await connection.QueryAsync<HistoryRow>(
            "SELECT module_key AS ModuleKey, sequence, script_identity AS ScriptIdentity, checksum, applied_at AS AppliedAt FROM socalytics_migrations.history ORDER BY applied_at, module_key, sequence")).ToArray();
        history.Length.ShouldBe(3);
        foreach (var script in scripts)
        {
            var row = history.Single(row => row.ModuleKey == script.ModuleKey.ToString() && row.ScriptIdentity == script.ScriptIdentity);
            row.Sequence.ShouldBe(script.Sequence);
            row.Checksum.ShouldBe(script.Checksum);
            row.AppliedAt.ShouldBeGreaterThan(DateTime.MinValue);
        }
    }

    [Fact]
    public async Task RepeatRunDoesNotChangeHistoryOrSchema()
    {
        await using var database = await StartDatabase();
        var script = Descriptor(ModuleKey.Club, 1, "initial", "CREATE TABLE public.once_only (id integer);");
        var orchestrator = Orchestrator(database.GetConnectionString(), script);
        orchestrator.Run();

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        var appliedAt = await connection.ExecuteScalarAsync<DateTime>("SELECT applied_at FROM socalytics_migrations.history");
        orchestrator.Run();

        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM socalytics_migrations.history")).ShouldBe(1);
        (await connection.ExecuteScalarAsync<DateTime>("SELECT applied_at FROM socalytics_migrations.history")).ShouldBe(appliedAt);
        (await connection.ExecuteScalarAsync<string>("SELECT to_regclass('public.once_only')::text")).ShouldBe("once_only");
    }

    [Fact]
    public async Task ChecksumConflictStopsAllLaterWork()
    {
        await using var database = await StartDatabase();
        var initial = Descriptor(ModuleKey.Club, 1, "initial", "CREATE TABLE public.original (id integer);");
        Orchestrator(database.GetConnectionString(), initial).Run();
        var changed = Descriptor(ModuleKey.Club, 1, "initial", "CREATE TABLE public.changed (id integer);");
        var later = Descriptor(ModuleKey.Club, 2, "later", "CREATE TABLE public.later (id integer);");

        var error = Should.Throw<MigrationConflictException>(() =>
            Orchestrator(database.GetConnectionString(), changed, later).Run());
        error.Message.ShouldContain("Club");
        error.Message.ShouldContain("initial");
        error.Message.ShouldNotContain(database.GetConnectionString());

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM socalytics_migrations.history")).ShouldBe(1);
        (await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('public.later')::text")).ShouldBeNull();
        (await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('public.changed')::text")).ShouldBeNull();
    }

    [Fact]
    public async Task FailedScriptRollsBackItsEffectsButRetainsEarlierScript()
    {
        await using var database = await StartDatabase();
        var first = Descriptor(ModuleKey.Club, 1, "first", "CREATE TABLE public.committed (id integer);");
        var failing = Descriptor(ModuleKey.Club, 2, "failing", """
            CREATE TABLE public.rolled_back (id integer);
            SELECT 1 / 0;
            """);

        var error = Should.Throw<InvalidOperationException>(() =>
            Orchestrator(database.GetConnectionString(), first, failing).Run());
        error.Message.ShouldContain("Club");
        error.Message.ShouldContain("failing");
        error.Message.ShouldNotContain("division");
        error.Message.ShouldNotContain(database.GetConnectionString());

        await using var connection = new NpgsqlConnection(database.GetConnectionString());
        (await connection.ExecuteScalarAsync<string>("SELECT to_regclass('public.committed')::text")).ShouldBe("committed");
        (await connection.ExecuteScalarAsync<string?>("SELECT to_regclass('public.rolled_back')::text")).ShouldBeNull();
        (await connection.QueryAsync<string>("SELECT script_identity FROM socalytics_migrations.history")).ShouldBe(["first"]);
    }

    private static async Task<PostgreSqlContainer> StartDatabase()
    {
        var database = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await database.StartAsync();
        return database;
    }

    private static MigrationOrchestrator Orchestrator(string connectionString, params MigrationDescriptor[] scripts)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(options =>
        {
            options.BootstrapConnectionString = connectionString;
            options.RuntimeConnectionString = connectionString;
        });
        services.AddSingleton<IModuleMigrationContributor>(new Contributor(ModuleKey.Club, scripts.Where(s => s.ModuleKey == ModuleKey.Club).ToArray()));
        services.AddSingleton<IModuleMigrationContributor>(new Contributor(ModuleKey.IdentityAccess, scripts.Where(s => s.ModuleKey == ModuleKey.IdentityAccess).ToArray()));
        return services.BuildServiceProvider().GetRequiredService<MigrationOrchestrator>();
    }

    private static MigrationDescriptor Descriptor(ModuleKey moduleKey, int sequence, string identity, string sql) =>
        new(moduleKey, sequence, identity, Encoding.UTF8.GetBytes(sql));

    private sealed class Contributor(ModuleKey moduleKey, MigrationDescriptor[] scripts) : IModuleMigrationContributor
    {
        public ModuleKey ModuleKey => moduleKey;
        public IReadOnlyList<MigrationDescriptor> GetMigrations() => scripts;
    }

    private sealed class HistoryRow
    {
        public string ModuleKey { get; set; } = string.Empty;
        public int Sequence { get; set; }
        public string ScriptIdentity { get; set; } = string.Empty;
        public string Checksum { get; set; } = string.Empty;
        public DateTime AppliedAt { get; set; }
    }
}

using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationOrchestrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Clean_database_is_migrated_in_order_and_recorded()
    {
        var cs = await NewDatabaseAsync();

        await MigrateAsync(cs, Registry(
            (PersistenceModuleKey.Registry, 1, "r1", "create table public.t_registry (id int);"),
            (PersistenceModuleKey.Club, 2, "c2", "create table public.t_club2 (id int);"),
            (PersistenceModuleKey.Club, 1, "c1", "create table public.t_club1 (id int);")));

        var rows = await HistoryAsync(cs);
        rows.Select(r => (r.Module, r.Sequence, r.Identity)).ShouldBe(
            [("club", 1, "c1"), ("club", 2, "c2"), ("registry", 1, "r1")], ignoreOrder: true);
        rows.OrderBy(r => r.AppliedAt).ThenBy(r => r.Identity, StringComparer.Ordinal)
            .Select(r => r.Identity).ShouldBe(["c1", "c2", "r1"]);
        rows.ShouldAllBe(r => r.Checksum.Length == 64);
    }

    [Fact]
    public async Task Unchanged_repeat_run_skips_everything()
    {
        var cs = await NewDatabaseAsync();
        var migrations = new[] { (PersistenceModuleKey.Club, 1, "c1", "create table public.t1 (id int);") };

        await MigrateAsync(cs, Registry(migrations));
        var before = await HistoryAsync(cs);
        await MigrateAsync(cs, Registry(migrations));

        (await HistoryAsync(cs)).ShouldBe(before);
    }

    [Fact]
    public async Task Checksum_conflict_fails_before_any_later_work()
    {
        var cs = await NewDatabaseAsync();
        await MigrateAsync(cs, Registry((PersistenceModuleKey.Club, 1, "c1", "create table public.t1 (id int);")));

        var error = await Should.ThrowAsync<MigrationException>(() => MigrateAsync(cs, Registry(
            (PersistenceModuleKey.Club, 1, "c1", "create table public.t1_changed (id int);"),
            (PersistenceModuleKey.Club, 2, "c2", "create table public.t2 (id int);"),
            (PersistenceModuleKey.Registry, 1, "r1", "create table public.t3 (id int);"))));

        error.Message.ShouldContain("c1");
        error.Message.ShouldContain("club");
        error.Message.ShouldNotContain("Password");
        (await HistoryAsync(cs)).Select(r => r.Identity).ShouldBe(["c1"]);
        (await TablesAsync(cs)).ShouldBe(["t1"]);
    }

    [Fact]
    public async Task Failed_script_rolls_back_and_keeps_earlier_scripts()
    {
        var cs = await NewDatabaseAsync();

        var error = await Should.ThrowAsync<MigrationException>(() => MigrateAsync(cs, Registry(
            (PersistenceModuleKey.Club, 1, "c1", "create table public.t1 (id int);"),
            (PersistenceModuleKey.Club, 2, "c2", "create table public.t2 (id int); select 1/0;"),
            (PersistenceModuleKey.Registry, 1, "r1", "create table public.t3 (id int);"))));

        error.Message.ShouldContain("c2");
        error.Message.ShouldNotContain("Password");
        (await HistoryAsync(cs)).Select(r => r.Identity).ShouldBe(["c1"]);
        (await TablesAsync(cs)).ShouldBe(["t1"]);
    }

    private async Task<string> NewDatabaseAsync()
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(container.GetConnectionString());
        await admin.ExecuteAsync($"create database {name}");
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private static async Task MigrateAsync(string connectionString, IMigrationContributor[] contributors)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.BootstrapConnectionString = connectionString);
        foreach (var contributor in contributors)
        {
            services.AddSingleton(contributor);
        }

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IMigrationOrchestrator>().MigrateAsync();
    }

    private static IMigrationContributor[] Registry(params (PersistenceModuleKey Module, int Sequence, string Identity, string Sql)[] items) =>
        [.. items.GroupBy(i => i.Module).Select(g => (IMigrationContributor)new Contributor(
            g.Key,
            [.. g.Select(i => new MigrationDescriptor(i.Module, i.Sequence, i.Identity, System.Text.Encoding.UTF8.GetBytes(i.Sql)))]))];

    private static async Task<List<HistoryRow>> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<HistoryRow>(
            "select module, sequence, identity, checksum, applied_at as AppliedAt from socalytics_migrations.history")).ToList();
    }

    private static async Task<List<string>> TablesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return (await connection.QueryAsync<string>(
            "select tablename from pg_tables where schemaname = 'public' order by 1")).ToList();
    }

    private sealed record HistoryRow(string Module, int Sequence, string Identity, string Checksum, DateTime AppliedAt);

    private sealed class Contributor(PersistenceModuleKey module, MigrationDescriptor[] migrations) : IMigrationContributor
    {
        public PersistenceModuleKey Module { get; } = module;

        public IEnumerable<MigrationDescriptor> GetMigrations() => migrations;
    }
}

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationOrchestrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    private sealed class Contributor(ModuleKey module, params MigrationDescriptor[] migrations) : IModuleMigrationContributor
    {
        public ModuleKey Module { get; } = module;
        public IReadOnlyList<MigrationDescriptor> Migrations { get; } = migrations;
    }

    private static MigrationDescriptor Migration(ModuleKey module, int sequence, string name, string sql) =>
        new(module, sequence, name, Encoding.UTF8.GetBytes(sql));

    private async Task<string> NewDatabaseAsync()
    {
        var name = "db_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    private static async Task RunAsync(string connectionString, params IModuleMigrationContributor[] contributors)
    {
        var services = new ServiceCollection();
        services.AddPlatformPersistence(o => o.ConnectionString = connectionString);
        foreach (var contributor in contributors)
        {
            services.AddSingleton(contributor);
        }

        await using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<MigrationRunner>().RunAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> HistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT module || '/' || sequence || '/' || script_name || '/' || checksum FROM socalytics_migrations.history ORDER BY applied_at, module, sequence", connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand($"SELECT to_regclass('{table}') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task CleanDatabaseIsMigratedInDeterministicOrder()
    {
        var cs = await NewDatabaseAsync();
        var club1 = Migration(ModuleKey.Club, 1, "z_first", "CREATE TABLE public.t_club1 (id int);");
        var club2 = Migration(ModuleKey.Club, 2, "a_second", "CREATE TABLE public.t_club2 (id int);");
        var rec1 = Migration(ModuleKey.Recordings, 1, "init", "CREATE TABLE public.t_rec1 (id int);");

        await RunAsync(cs, new Contributor(ModuleKey.Recordings, rec1), new Contributor(ModuleKey.Club, club2, club1));

        (await HistoryAsync(cs)).ShouldBe([
            $"club/1/z_first/{club1.Checksum}",
            $"club/2/a_second/{club2.Checksum}",
            $"recordings/1/init/{rec1.Checksum}"]);
    }

    [Fact]
    public async Task UnchangedRepeatStartupChangesNothing()
    {
        var cs = await NewDatabaseAsync();
        var contributor = new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "init", "CREATE TABLE public.t_repeat (id int);"));

        await RunAsync(cs, contributor);
        var before = await HistoryAsync(cs);
        await RunAsync(cs, contributor);

        (await HistoryAsync(cs)).ShouldBe(before);
    }

    [Fact]
    public async Task ChecksumConflictFailsBeforeLaterWorkWithoutLeakingCredentials()
    {
        var cs = await NewDatabaseAsync();
        await RunAsync(cs, new Contributor(ModuleKey.Club, Migration(ModuleKey.Club, 1, "init", "CREATE TABLE public.t_conflict (id int);")));

        var changed = new Contributor(ModuleKey.Club,
            Migration(ModuleKey.Club, 1, "init", "CREATE TABLE public.t_conflict (id int, extra int);"),
            Migration(ModuleKey.Club, 2, "later", "CREATE TABLE public.t_later (id int);"));

        var ex = await Should.ThrowAsync<MigrationException>(() => RunAsync(cs, changed));

        ex.Message.ShouldContain("club");
        ex.Message.ShouldContain("init");
        ex.Message.ShouldNotContain("Password", Case.Insensitive);
        (await TableExistsAsync(cs, "public.t_later")).ShouldBeFalse();
        (await HistoryAsync(cs)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task FailedScriptRollsBackAndKeepsEarlierScripts()
    {
        var cs = await NewDatabaseAsync();
        var contributor = new Contributor(ModuleKey.Club,
            Migration(ModuleKey.Club, 1, "ok", "CREATE TABLE public.t_ok (id int);"),
            Migration(ModuleKey.Club, 2, "bad", "CREATE TABLE public.t_partial (id int); SELECT * FROM public.missing_table;"),
            Migration(ModuleKey.Club, 3, "never", "CREATE TABLE public.t_never (id int);"));

        var ex = await Should.ThrowAsync<MigrationException>(() => RunAsync(cs, contributor));

        ex.Message.ShouldContain("bad");
        ex.Message.ShouldNotContain("Password", Case.Insensitive);
        (await TableExistsAsync(cs, "public.t_ok")).ShouldBeTrue();
        (await TableExistsAsync(cs, "public.t_partial")).ShouldBeFalse();
        (await TableExistsAsync(cs, "public.t_never")).ShouldBeFalse();
        (await HistoryAsync(cs)).Count.ShouldBe(1);
    }
}

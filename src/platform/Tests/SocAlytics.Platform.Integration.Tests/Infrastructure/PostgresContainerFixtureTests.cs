using Npgsql;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed class PostgresContainerFixtureTests(PostgresContainerFixture fixture)
{
    private static async Task<T?> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ServerMajorVersionIs18()
    {
        var version = await ScalarAsync<string>(fixture.SuperuserConnectionString, "SHOW server_version_num");
        (int.Parse(version!) / 10000).ShouldBe(18);
    }

    [Fact]
    public async Task IsolatedDatabasesHaveDifferentNames()
    {
        await using var first = await fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);
        await using var second = await fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        first.Name.ShouldNotBe(second.Name);
    }

    [Fact]
    public async Task ConnectionsUseTheExpectedRoles()
    {
        await using var database = await fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync<string>(database.MigratorConnectionString, "SELECT current_user")).ShouldBe("socalytics_migrator");
        (await ScalarAsync<string>(database.AppConnectionString, "SELECT current_user")).ShouldBe("socalytics_app");
    }

    [Fact]
    public async Task IsolatedDatabaseIsOwnedByMigratorAndClosedToRuntimeCreation()
    {
        await using var database = await fixture.CreateDatabaseAsync(TestContext.Current.CancellationToken);

        (await ScalarAsync<string>(database.SuperuserConnectionString, OwnerSql(database.Name))).ShouldBe("socalytics_migrator");
        (await ScalarAsync<bool>(database.SuperuserConnectionString, PrivilegeSql(database.Name, "CREATE"))).ShouldBeFalse();
        (await ScalarAsync<bool>(database.SuperuserConnectionString, PrivilegeSql(database.Name, "TEMPORARY"))).ShouldBeFalse();
    }

    [Fact]
    public async Task InitScriptConfiguredTheSocalyticsDatabase()
    {
        (await ScalarAsync<string>(fixture.SuperuserConnectionString, OwnerSql("socalytics"))).ShouldBe("socalytics_migrator");
    }

    private static string OwnerSql(string database) =>
        $"SELECT pg_catalog.pg_get_userbyid(datdba) FROM pg_database WHERE datname = '{database}'";

    private static string PrivilegeSql(string database, string privilege) =>
        $"SELECT has_database_privilege('socalytics_app', '{database}', '{privilege}')";
}

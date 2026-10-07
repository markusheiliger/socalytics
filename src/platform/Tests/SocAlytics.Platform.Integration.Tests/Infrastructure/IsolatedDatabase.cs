using Npgsql;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed class IsolatedDatabase : IAsyncDisposable
{
    private IsolatedDatabase(DedicatedPostgres server, string name)
    {
        Server = server;
        Name = name;
    }

    private DedicatedPostgres Server { get; }

    public string Name { get; }

    public string MigratorConnectionString =>
        Server.BuildConnectionString(Name, "socalytics_migrator", Server.MigratorPassword);

    public string AppConnectionString =>
        Server.BuildConnectionString(Name, "socalytics_app", Server.AppPassword);

    public string SuperuserConnectionString => Server.SuperuserConnectionString;

    internal static async Task<IsolatedDatabase> CreateAsync(DedicatedPostgres server, CancellationToken cancellationToken)
    {
        var name = $"t_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(server.SuperuserConnectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var sql in new[]
        {
            $"CREATE DATABASE \"{name}\" OWNER socalytics_migrator",
            $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC",
            $"GRANT CONNECT ON DATABASE \"{name}\" TO socalytics_app",
        })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return new IsolatedDatabase(server, name);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(Server.SuperuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}

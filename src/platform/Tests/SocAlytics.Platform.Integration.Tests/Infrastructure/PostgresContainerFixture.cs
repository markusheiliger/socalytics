using System.Security.Cryptography;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

[assembly: AssemblyFixture(typeof(SocAlytics.Platform.Integration.Tests.Infrastructure.PostgresContainerFixture))]

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed class PostgresContainerFixture : IAsyncLifetime
{
    private DedicatedPostgres? _shared;

    public string MigratorPassword => Shared.MigratorPassword;

    public string AppPassword => Shared.AppPassword;

    public string SuperuserConnectionString => Shared.SuperuserConnectionString;

    public string MigratorConnectionString => Shared.MigratorConnectionString;

    private DedicatedPostgres Shared =>
        _shared ?? throw new InvalidOperationException("The PostgreSQL container has not been started.");

    public async ValueTask InitializeAsync()
    {
        // Fails visibly when Docker is unavailable; there is no fallback database.
        _shared = await StartContainerAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_shared is not null)
        {
            await _shared.DisposeAsync();
        }
    }

    public Task<IsolatedDatabase> CreateDatabaseAsync(CancellationToken cancellationToken) =>
        IsolatedDatabase.CreateAsync(Shared, cancellationToken);

    public Task<DedicatedPostgres> CreateDedicatedContainerAsync(CancellationToken cancellationToken) =>
        StartContainerAsync(cancellationToken);

    private static async Task<DedicatedPostgres> StartContainerAsync(CancellationToken cancellationToken)
    {
        var migratorPassword = GeneratePassword();
        var appPassword = GeneratePassword();
        var initScript = Path.Combine(AppContext.BaseDirectory, "PostgresInit", "01-socalytics-roles.sh");

        var container = new PostgreSqlBuilder("postgres:18")
            .WithDatabase("socalytics")
            .WithEnvironment("SOCALYTICS_MIGRATOR_PASSWORD", migratorPassword)
            .WithEnvironment("SOCALYTICS_APP_PASSWORD", appPassword)
            .WithResourceMapping(new FileInfo(initScript), "/docker-entrypoint-initdb.d/")
            .Build();

        await container.StartAsync(cancellationToken);
        return new DedicatedPostgres(container, migratorPassword, appPassword);
    }

    private static string GeneratePassword() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public sealed class DedicatedPostgres : IAsyncDisposable
{
    private readonly PostgreSqlContainer _container;

    internal DedicatedPostgres(PostgreSqlContainer container, string migratorPassword, string appPassword)
    {
        _container = container;
        MigratorPassword = migratorPassword;
        AppPassword = appPassword;
    }

    public string MigratorPassword { get; }

    public string AppPassword { get; }

    public string SuperuserConnectionString => _container.GetConnectionString();

    public string MigratorConnectionString => BuildConnectionString("socalytics", "socalytics_migrator", MigratorPassword);

    public string AppConnectionString => BuildConnectionString("socalytics", "socalytics_app", AppPassword);

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        _container.PauseAsync(cancellationToken);

    public Task UnpauseAsync(CancellationToken cancellationToken = default) =>
        _container.UnpauseAsync(cancellationToken);

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    internal string BuildConnectionString(string database, string username, string password) =>
        new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Username = username,
            Password = password,
            Pooling = false,
        }.ConnectionString;
}

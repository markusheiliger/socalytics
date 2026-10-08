using Microsoft.Extensions.Hosting;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

internal sealed record MigratorRunResult(MigratorExitCode ExitCode, IReadOnlyList<CapturedLogEntry> Logs);

internal static class MigratorHarness
{
    public static Task<MigratorRunResult> RunAsync(
        IsolatedDatabase database,
        MigrationCatalog catalog,
        CancellationToken cancellationToken,
        Action<IHost>? onStarted = null,
        params string[] extraArguments) =>
        RunAsync(database, () => catalog, cancellationToken, onStarted, extraArguments);

    public static async Task<MigratorRunResult> RunAsync(
        IsolatedDatabase database,
        Func<MigrationCatalog> catalogFactory,
        CancellationToken cancellationToken,
        Action<IHost>? onStarted = null,
        params string[] extraArguments)
    {
        var logs = new CapturingLoggerProvider();
        string[] args =
        [
            $"--ConnectionStrings:socalytics-migrator={database.MigratorConnectionString}",
            .. extraArguments,
        ];

        var code = await MigratorEntryPoint.RunAsync(args, cancellationToken, catalogFactory, logs, onStarted);
        return new MigratorRunResult(code, logs.Entries);
    }
}

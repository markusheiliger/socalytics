using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Migrator;

internal static class MigratorEntryPoint
{
    public static Task<MigratorExitCode> RunAsync(string[] args, CancellationToken cancellationToken) =>
        RunAsync(args, cancellationToken, null, null, null);

    internal static async Task<MigratorExitCode> RunAsync(
        string[] args,
        CancellationToken cancellationToken,
        Func<MigrationCatalog>? catalogFactory,
        ILoggerProvider? extraLoggerProvider,
        Action<IHost>? onStarted)
    {
        IHost? host = null;
        ILogger? logger = null;
        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.AddServiceDefaults();
            if (extraLoggerProvider is not null)
            {
                builder.Logging.AddProvider(extraLoggerProvider);
            }

            host = builder.Build();
            logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(MigratorLog.CategoryName);

            var connectionString = builder.Configuration.GetConnectionString(DatabaseConnectionNames.Migrator);
            var options = builder.Configuration.GetSection(MigratorOptions.SectionName).Get<MigratorOptions>()
                ?? new MigratorOptions();
            if (string.IsNullOrWhiteSpace(connectionString) || !options.IsValid())
            {
                MigratorLog.Failed(logger, "configuration");
                return await StopAsync(host, MigratorExitCode.ConfigurationInvalid);
            }

            MigrationCatalog catalog;
            try
            {
                catalog = catalogFactory is null ? MigrationCatalog.Platform : catalogFactory();
            }
            catch (Exception ex) when (ex is MigrationCatalogException
                or TypeInitializationException { InnerException: MigrationCatalogException })
            {
                MigratorLog.Failed(logger, "catalog-invalid");
                return await StopAsync(host, MigratorExitCode.CatalogInvalid);
            }

            await host.StartAsync(CancellationToken.None);
            onStarted?.Invoke(host);

            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, lifetime.ApplicationStopping);

            var runner = new MigrationRunner(logger, options, catalog, connectionString);
            var code = await runner.RunAsync(linked.Token);
            return await StopAsync(host, code);
        }
        catch (OperationCanceledException)
        {
            return MigratorExitCode.Cancelled;
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                MigratorLog.Failed(logger, "unexpected", exceptionType: ex.GetType().Name);
            }

            return MigratorExitCode.UnexpectedError;
        }
        finally
        {
            host?.Dispose();
        }
    }

    private static async Task<MigratorExitCode> StopAsync(IHost host, MigratorExitCode code)
    {
        try
        {
            await host.StopAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // The run result takes precedence over a telemetry flush failure.
        }

        return code;
    }
}

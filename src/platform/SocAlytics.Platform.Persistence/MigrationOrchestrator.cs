using DbUp;
using DbUp.Engine;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

internal sealed class MigrationOrchestrator(
    IBootstrapDatabaseConnectionFactory connections,
    MigrationCatalog catalog,
    string bootstrapConnectionString)
{
    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        MigrationDescriptor? current = null;
        try
        {
            await using var connection = await connections.OpenConnectionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand(
                """
                CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
                REVOKE ALL ON SCHEMA socalytics_migrations FROM PUBLIC;
                CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
                    module_key text NOT NULL,
                    sequence integer NOT NULL CHECK (sequence > 0),
                    script_identity text NOT NULL,
                    checksum text NOT NULL CHECK (checksum ~ '^[0-9a-f]{64}$'),
                    applied_at timestamp with time zone NOT NULL DEFAULT clock_timestamp(),
                    PRIMARY KEY (module_key, script_identity),
                    UNIQUE (module_key, sequence)
                );
                REVOKE ALL ON TABLE socalytics_migrations.history FROM PUBLIC;
                """,
                connection))
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var applied = new HashSet<MigrationDescriptor>();
            await using (var command = new NpgsqlCommand(
                "SELECT module_key, sequence, script_identity, checksum FROM socalytics_migrations.history",
                connection))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var module = reader.GetString(0);
                    var sequence = reader.GetInt32(1);
                    var identity = reader.GetString(2);
                    var checksum = reader.GetString(3);
                    foreach (var migration in catalog.Migrations.Where(migration => migration.Module.Key == module))
                    {
                        if (migration.Identity == identity)
                        {
                            if (migration.Sequence != sequence || migration.Checksum != checksum)
                            {
                                throw new MigrationException(
                                    $"Migration conflict for module '{module}', script '{migration.Identity}'.");
                            }

                            applied.Add(migration);
                        }
                        else if (migration.Sequence == sequence)
                        {
                            throw new MigrationException(
                                $"Migration conflict for module '{module}', script '{migration.Identity}'.");
                        }
                    }
                }
            }

            foreach (var module in PersistenceModuleIdentity.All)
            foreach (var migration in catalog.Migrations
                .Where(migration => migration.Module == module && !applied.Contains(migration))
                .OrderBy(migration => migration.Sequence))
            {
                cancellationToken.ThrowIfCancellationRequested();
                current = migration;
                using var script = migration.OpenScript();
                using var reader = new StreamReader(script);
                var engine = DeployChanges.To
                    .PostgresqlDatabase(bootstrapConnectionString)
                    .WithScripts(new SqlScript(migration.Identity, await reader.ReadToEndAsync(cancellationToken)))
                    .WithVariablesDisabled()
                    .WithTransactionPerScript()
                    .JournalTo((_, _) => new MigrationJournal(migration))
                    .LogToNowhere()
                    .Build();

                if (!engine.PerformUpgrade().Successful)
                {
                    throw new MigrationException(
                        $"Migration failed for module '{module.Key}', script '{migration.Identity}'.");
                }
            }
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Do not retain provider errors: they may contain SQL, credentials, or connection details.
            throw new MigrationException(current is null
                ? "Migration preparation failed."
                : $"Migration failed for module '{current.Module.Key}', script '{current.Identity}'.");
        }
    }
}

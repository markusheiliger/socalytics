namespace SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;

internal static class MigrationHistorySql
{
    public const string CreateSchema = "CREATE SCHEMA socalytics_migrations";

    public const string CreateTable = """
        CREATE TABLE socalytics_migrations.history (
            sequence integer PRIMARY KEY CHECK (sequence BETWEEN 1 AND 9999),
            identity text NOT NULL UNIQUE,
            checksum text NOT NULL CHECK (checksum ~ '^sha-256:[0-9a-f]{64}$'),
            applied_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    public const string GrantSchemaUsage = "GRANT USAGE ON SCHEMA socalytics_migrations TO socalytics_app";

    public const string GrantSelect = "GRANT SELECT ON socalytics_migrations.history TO socalytics_app";

    public const string TableExists = "SELECT to_regclass('socalytics_migrations.history') IS NOT NULL";

    public const string SelectIdentities = "SELECT identity FROM socalytics_migrations.history ORDER BY sequence";

    public const string SelectEntries =
        "SELECT sequence, identity, checksum FROM socalytics_migrations.history ORDER BY sequence";

    public const string Insert =
        "INSERT INTO socalytics_migrations.history (sequence, identity, checksum) VALUES (@sequence, @identity, @checksum)";
}

using System.Data;
using DbUp.Engine;
using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// The DbUp journal backed by <c>socalytics_migrations.history</c>. DbUp invokes the store call on the
/// connection and transaction that ran the script, so the history row commits with the script's effects.
/// </summary>
internal sealed class MigrationJournal(IReadOnlyDictionary<string, MigrationDescriptor> byScriptName, string[] executed) : IJournal
{
    public const string Schema = "socalytics_migrations";

    private const string EnsureSql = """
        CREATE SCHEMA IF NOT EXISTS socalytics_migrations;
        CREATE TABLE IF NOT EXISTS socalytics_migrations.history (
            module text NOT NULL,
            sequence integer NOT NULL,
            script_name text NOT NULL,
            checksum text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT pk_history PRIMARY KEY (module, script_name),
            CONSTRAINT uq_history_module_sequence UNIQUE (module, sequence)
        );
        """;

    public static string ScriptKey(MigrationDescriptor migration) => migration.Module.Name + "/" + migration.ScriptName;

    public static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(EnsureSql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<AppliedMigration>> ReadAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new List<AppliedMigration>();
        await using var command = new NpgsqlCommand(
            "SELECT module, sequence, script_name, checksum FROM socalytics_migrations.history", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(new AppliedMigration(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3)));
        }

        return applied;
    }

    public string[] GetExecutedScripts() => executed;

    public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        var migration = byScriptName[script.Name];
        using var command = dbCommandFactory();
        command.CommandText =
            "INSERT INTO socalytics_migrations.history (module, sequence, script_name, checksum) VALUES (@module, @sequence, @script, @checksum)";
        AddParameter(command, "module", migration.Module.Name);
        AddParameter(command, "sequence", migration.Sequence);
        AddParameter(command, "script", migration.ScriptName);
        AddParameter(command, "checksum", migration.Checksum);
        command.ExecuteNonQuery();
    }

    public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
    {
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

internal sealed record AppliedMigration(string Module, int Sequence, string ScriptName, string Checksum);

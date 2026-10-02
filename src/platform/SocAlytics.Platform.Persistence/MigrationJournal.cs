using System.Data;
using DbUp.Engine;

namespace SocAlytics.Platform.Persistence;

internal sealed class MigrationJournal(MigrationDescriptor migration) : IJournal
{
    public string[] GetExecutedScripts() => [];

    public void EnsureTableExistsAndIsLatestVersion(Func<IDbCommand> dbCommandFactory)
    {
        // The complete history is initialized and checked before any script is selected.
    }

    public void StoreExecutedScript(SqlScript script, Func<IDbCommand> dbCommandFactory)
    {
        // DbUp supplies the command factory bound to the script's transaction.
        using var command = dbCommandFactory();
        command.CommandText =
            """
            INSERT INTO socalytics_migrations.history (module_key, sequence, script_identity, checksum)
            VALUES (@module, @sequence, @identity, @checksum)
            """;
        AddParameter(command, "module", migration.Module.Key);
        AddParameter(command, "sequence", migration.Sequence);
        AddParameter(command, "identity", migration.Identity);
        AddParameter(command, "checksum", migration.Checksum);
        command.ExecuteNonQuery();
    }

    private static void AddParameter(IDbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

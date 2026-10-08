using System.Data;
using System.Globalization;
using DbUp.Engine;
using DbUp.Engine.Output;
using DbUp.Engine.Transactions;
using DbUp.Postgresql;
using DbUp.Support;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Migrator;

internal sealed class SocAlyticsHistoryJournal(
    Func<IConnectionManager> connectionManager,
    Func<IUpgradeLog> log)
    : TableJournal(connectionManager, log, new PostgresqlObjectParser(), "socalytics_migrations", "history")
{
    protected override string CreateSchemaTableSql(string quotedPrimaryKeyName) =>
        string.Join(
            ";\n",
            MigrationHistorySql.CreateSchema,
            MigrationHistorySql.CreateTable,
            MigrationHistorySql.GrantSchemaUsage,
            MigrationHistorySql.GrantSelect);

    protected override string GetInsertJournalEntrySql(string scriptName, string applied) => MigrationHistorySql.Insert;

    protected override string DoesTableExistSql() => MigrationHistorySql.TableExistsAsRow;

    protected override string GetJournalEntriesSql() => MigrationHistorySql.SelectIdentities;

    protected override IDbCommand GetInsertScriptCommand(Func<IDbCommand> dbCommandFactory, SqlScript script)
    {
        var command = dbCommandFactory();
        command.CommandText = MigrationHistorySql.Insert;

        AddParameter(command, "sequence", DbType.Int32, int.Parse(script.Name.AsSpan(0, 4), CultureInfo.InvariantCulture));
        AddParameter(command, "identity", DbType.String, script.Name);
        AddParameter(command, "checksum", DbType.String, MigrationChecksum.Compute(script.Contents));
        return command;
    }

    private static void AddParameter(IDbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

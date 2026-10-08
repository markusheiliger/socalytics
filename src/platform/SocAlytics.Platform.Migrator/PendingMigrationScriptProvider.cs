using DbUp.Engine;
using DbUp.Engine.Transactions;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;

namespace SocAlytics.Platform.Migrator;

internal sealed class PendingMigrationScriptProvider(IEnumerable<MigrationScript> pending) : IScriptProvider
{
    private readonly MigrationScript[] _pending = pending.OrderBy(script => script.Sequence).ToArray();

    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager) =>
        _pending.Select(script => new SqlScript(script.Identity, script.Content));
}

using Npgsql;

namespace SocAlytics.Platform.Persistence;

public interface IModuleTransactionExecutor
{
    Task ExecuteAsync(
        ModuleKey moduleKey,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);
}

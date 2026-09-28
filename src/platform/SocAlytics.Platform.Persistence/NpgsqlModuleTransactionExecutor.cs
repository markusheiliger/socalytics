using Npgsql;
using SocAlytics.Platform.Persistence.Connections;

namespace SocAlytics.Platform.Persistence;

internal sealed class NpgsqlModuleTransactionExecutor(IModuleConnectionFactory connectionFactory) : IModuleTransactionExecutor
{
    public async Task ExecuteAsync(
        ModuleKey moduleKey,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = connectionFactory.CreateConnection(moduleKey);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }
}

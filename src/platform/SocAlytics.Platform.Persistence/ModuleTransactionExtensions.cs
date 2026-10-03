using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class ModuleTransactionExtensions
{
    /// <summary>
    /// Runs <paramref name="operation"/> on a module-scoped connection inside one explicit transaction. The
    /// connection and transaction are passed to the operation, which must hand them to every command; no ambient
    /// transaction is used. The transaction commits only when the operation succeeds and is rolled back when it
    /// throws or is cancelled, leaving the connection free of an active transaction when it returns to the pool.
    /// </summary>
    public static async Task<T> ExecuteInTransactionAsync<T>(
        this IModuleConnectionFactory factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task ExecuteInTransactionAsync(
        this IModuleConnectionFactory factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await factory.ExecuteInTransactionAsync(
            async (connection, transaction, token) =>
            {
                await operation(connection, transaction, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RollbackQuietlyAsync(NpgsqlTransaction transaction)
    {
        try
        {
            // Not cancellable: a cancelled operation must still release its transaction.
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original failure is the one to surface; a broken connection is discarded on disposal.
        }
    }
}

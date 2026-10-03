using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class ModuleTransactions
{
    /// <summary>
    /// Runs the operation on a module-scoped connection inside one explicit transaction. The caller passes the
    /// provided connection and transaction to every command. Commits on success; rolls back on exception or cancellation.
    /// </summary>
    public static async Task<TResult> ExecuteInTransactionAsync<TModule, TResult>(
        this IModuleConnectionFactory<TModule> factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await operation(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            // Rollback must not observe the caller's cancellation, otherwise the transaction would stay open.
            await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    public static async Task ExecuteInTransactionAsync<TModule>(
        this IModuleConnectionFactory<TModule> factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await factory.ExecuteInTransactionAsync<TModule, bool>(
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
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // A broken connection already discarded the transaction server-side; keep the original failure.
        }
    }
}

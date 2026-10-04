using Npgsql;

namespace SocAlytics.Platform.Persistence;

public static class ModuleTransactionExtensions
{
    /// <summary>
    /// Runs the operation on a module-scoped connection inside one explicit transaction. The transaction commits only
    /// when the operation completes and rolls back on any exception or cancellation. No ambient transaction is used.
    /// </summary>
    public static async Task<T> ExecuteInTransactionAsync<T>(
        this IModuleConnectionFactory factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = await factory.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            await RollbackQuietlyAsync(transaction);
            throw;
        }
    }

    public static Task ExecuteInTransactionAsync(
        this IModuleConnectionFactory factory,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return factory.ExecuteInTransactionAsync<object?>(
            async (connection, transaction, ct) =>
            {
                await operation(connection, transaction, ct);
                return null;
            },
            cancellationToken);
    }

    // Rollback must not observe the caller's cancelled token; a failed rollback leaves the connection to be discarded on dispose.
    private static async Task RollbackQuietlyAsync(NpgsqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }
}

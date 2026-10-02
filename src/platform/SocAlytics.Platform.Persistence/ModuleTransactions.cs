using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Runs one operation inside an explicit module-scoped transaction. The connection and transaction are
/// passed to the caller, which must hand the transaction to every command; no ambient transaction exists.
/// </summary>
public interface IModuleTransactionExecutor
{
    PersistenceModuleKey Module { get; }

    Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default);

    Task<T> ExecuteAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default);
}

internal sealed class ModuleTransactionExecutor(IModuleConnectionFactory connections) : IModuleTransactionExecutor
{
    public PersistenceModuleKey Module => connections.Module;

    public async Task ExecuteAsync(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteAsync<object?>(async (connection, transaction, ct) =>
        {
            await operation(connection, transaction, ct);
            return null;
        }, cancellationToken);
    }

    public async Task<T> ExecuteAsync<T>(
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var result = await operation(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            // Rollback must not observe the caller's (possibly cancelled) token.
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ObjectDisposedException)
            {
                // The original failure is the one that matters; a broken connection is discarded on dispose.
            }

            throw;
        }
    }
}

/// <summary>Signals that an update guarded by an expected version matched no row.</summary>
public sealed class ConcurrencyConflictException()
    : InvalidOperationException("The record was modified concurrently or no longer exists; its expected version did not match.");

/// <summary>
/// Standardizes the affected-row check for version-guarded updates. Modules own the SQL
/// (<c>UPDATE ... SET version = version + 1 WHERE id = @id AND version = @expected</c>).
/// </summary>
public static class OptimisticConcurrency
{
    public static void EnsureUpdated(int affectedRows)
    {
        if (affectedRows == 1)
        {
            return;
        }

        if (affectedRows == 0)
        {
            throw new ConcurrencyConflictException();
        }

        throw new InvalidOperationException("A version-guarded update affected more than one row.");
    }
}

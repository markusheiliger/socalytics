using Npgsql;

namespace SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;

internal sealed class MigrationLockResult : IAsyncDisposable
{
    private readonly NpgsqlConnection? _connection;

    private MigrationLockResult(NpgsqlConnection? connection) => _connection = connection;

    public bool Acquired => _connection is not null;

    public static MigrationLockResult TimedOut { get; } = new(null);

    internal static MigrationLockResult Held(NpgsqlConnection connection) => new(connection);

    public async ValueTask DisposeAsync()
    {
        if (_connection is null || _connection.State != System.Data.ConnectionState.Open)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand(MigrationLock.UnlockSql, _connection);
            await command.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
            // Closing the session releases the lock anyway.
        }
    }
}

internal static class MigrationLock
{
    internal const string TryLockSql = "SELECT pg_try_advisory_lock(5459779, 1)";
    internal const string UnlockSql = "SELECT pg_advisory_unlock(5459779, 1)";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Polls the advisory lock on an open, dedicated connection. Returns a result with
    /// <see cref="MigrationLockResult.Acquired"/> false on timeout; throws
    /// <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public static async Task<MigrationLockResult> AcquireAsync(
        NpgsqlConnection connection,
        TimeSpan waitTimeout,
        Action? onWaiting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var deadline = TimeProvider.System.GetTimestamp();
        var waitingReported = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using (var command = new NpgsqlCommand(TryLockSql, connection))
            {
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                {
                    return MigrationLockResult.Held(connection);
                }
            }

            if (!waitingReported)
            {
                waitingReported = true;
                onWaiting?.Invoke();
            }

            var remaining = waitTimeout - TimeProvider.System.GetElapsedTime(deadline);
            if (remaining <= TimeSpan.Zero)
            {
                return MigrationLockResult.TimedOut;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }
}

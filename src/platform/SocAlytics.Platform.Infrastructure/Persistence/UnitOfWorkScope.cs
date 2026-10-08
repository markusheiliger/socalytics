using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal sealed class UnitOfWorkScope(DbSession session) : IUnitOfWorkScope
{
    private bool _completed;
    private bool _disposed;
    private bool _committed;

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        Complete();
        try
        {
            await session.RequireTransaction().CommitAsync(cancellationToken);
            _committed = true;
        }
        catch
        {
            await RollbackQuietlyAsync();
            throw;
        }
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        Complete();
        await session.RequireTransaction().RollbackAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_committed)
        {
            await RollbackQuietlyAsync();
        }

        await session.EndAsync();
    }

    private void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The unit of work has already been committed or rolled back.");
        }

        _completed = true;
    }

    private async ValueTask RollbackQuietlyAsync()
    {
        var transaction = session.Transaction;
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException)
        {
            // The transaction already completed or the connection is gone; disposal releases it.
        }
    }
}

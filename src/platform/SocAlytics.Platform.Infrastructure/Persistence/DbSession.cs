using System.Data;
using Npgsql;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal sealed class DbSession(PlatformDataSource platformDataSource) : IUnitOfWork, IDbSession, IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private UnitOfWorkScope? _scope;

    public NpgsqlTransaction? Transaction { get; private set; }

    public async ValueTask<NpgsqlConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return _connection;
        }

        if (!platformDataSource.TryGetDataSource(out var dataSource))
        {
            throw new InvalidOperationException("The platform database connection is not configured.");
        }

        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return _connection;
    }

    public NpgsqlTransaction RequireTransaction() =>
        Transaction ?? throw new InvalidOperationException("This operation requires an active unit of work.");

    public async Task<IUnitOfWorkScope> BeginAsync(CancellationToken cancellationToken)
    {
        if (_scope is not null)
        {
            throw new InvalidOperationException("A unit of work is already active in this scope.");
        }

        var scope = new UnitOfWorkScope(this);
        _scope = scope;
        try
        {
            var connection = await GetConnectionAsync(cancellationToken);
            Transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        }
        catch
        {
            await EndAsync();
            throw;
        }

        return scope;
    }

    internal async ValueTask EndAsync()
    {
        var transaction = Transaction;
        var connection = _connection;
        Transaction = null;
        _connection = null;
        _scope = null;

        try
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is not null)
        {
            await _scope.DisposeAsync();
        }

        await EndAsync();
    }
}

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal sealed class PlatformDataSource(IConfiguration configuration) : IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private NpgsqlDataSource? _dataSource;
    private bool _disposed;

    public bool TryGetDataSource([NotNullWhen(true)] out NpgsqlDataSource? dataSource)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_dataSource is null)
            {
                var connectionString = configuration.GetConnectionString(DatabaseConnectionNames.Runtime);
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    dataSource = null;
                    return false;
                }

                // A hung server must not hold a connection attempt or cancellation beyond the readiness budget.
                var builder = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    Timeout = 3,
                    CancellationTimeout = 1000,
                };
                _dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            }

            dataSource = _dataSource;
            return true;
        }
    }

    public void Dispose()
    {
        NpgsqlDataSource? toDispose = TakeDataSource();
        toDispose?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        NpgsqlDataSource? toDispose = TakeDataSource();
        return toDispose is null ? ValueTask.CompletedTask : toDispose.DisposeAsync();
    }

    private NpgsqlDataSource? TakeDataSource()
    {
        lock (_gate)
        {
            _disposed = true;
            var current = _dataSource;
            _dataSource = null;
            return current;
        }
    }
}

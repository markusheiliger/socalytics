using Npgsql;

namespace SocAlytics.Platform.Infrastructure.Persistence;

internal interface IDbSession
{
    NpgsqlTransaction? Transaction { get; }

    ValueTask<NpgsqlConnection> GetConnectionAsync(CancellationToken cancellationToken);

    NpgsqlTransaction RequireTransaction();
}

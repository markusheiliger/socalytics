using Npgsql;

namespace SocAlytics.Platform.Persistence;

/// <summary>Opens normal-runtime sessions bound to one fixed module identity.</summary>
public interface IModuleConnectionFactory
{
    PersistenceModuleKey Module { get; }

    Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default);
}

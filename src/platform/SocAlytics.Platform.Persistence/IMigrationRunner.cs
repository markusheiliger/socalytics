namespace SocAlytics.Platform.Persistence;

/// <summary>Applies all registered migrations. Intended for host startup only.</summary>
public interface IMigrationRunner
{
    /// <summary>
    /// Verifies every applied checksum, then applies pending migrations in module and sequence order.
    /// </summary>
    /// <exception cref="MigrationException">Preflight or a migration failed; the message is sanitized.</exception>
    Task RunAsync(CancellationToken cancellationToken = default);
}

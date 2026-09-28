namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Configures the restricted bootstrap (admin/migration) and runtime (module-scoped)
/// connection strings. The bootstrap connection is never exposed to module services; only
/// the runtime connection backs module-scoped sessions.
/// </summary>
public sealed class PersistenceOptions
{
    public string BootstrapConnectionString { get; set; } = string.Empty;

    public string RuntimeConnectionString { get; set; } = string.Empty;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(BootstrapConnectionString))
        {
            throw new InvalidOperationException($"{nameof(BootstrapConnectionString)} must be configured.");
        }

        if (string.IsNullOrWhiteSpace(RuntimeConnectionString))
        {
            throw new InvalidOperationException($"{nameof(RuntimeConnectionString)} must be configured.");
        }
    }
}

namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Implemented internally by each owning capability module to contribute its schema-only
/// embedded migrations to the shared orchestrator without exposing module SQL publicly.
/// </summary>
public interface IModuleMigrationContributor
{
    ModuleKey ModuleKey { get; }

    IReadOnlyList<MigrationDescriptor> GetMigrations();
}

namespace SocAlytics.Platform.Persistence;

/// <summary>Supplies the migrations of exactly one module.</summary>
public interface IMigrationContributor
{
    PersistenceModuleKey Module { get; }

    IReadOnlyList<MigrationDescriptor> GetMigrations();
}

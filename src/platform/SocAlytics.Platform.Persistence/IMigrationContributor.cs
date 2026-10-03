namespace SocAlytics.Platform.Persistence;

/// <summary>Supplies the migrations owned by exactly one adopted module.</summary>
public interface IMigrationContributor
{
    PersistenceModule Module { get; }

    IEnumerable<MigrationDescriptor> GetMigrations();
}

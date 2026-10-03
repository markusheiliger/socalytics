namespace SocAlytics.Platform.Persistence;

/// <summary>Supplies the migrations owned by exactly one module.</summary>
public interface IModuleMigrationContributor
{
    ModuleKey Module { get; }

    IReadOnlyList<MigrationDescriptor> Migrations { get; }
}

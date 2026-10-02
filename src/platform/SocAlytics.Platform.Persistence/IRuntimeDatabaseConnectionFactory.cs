namespace SocAlytics.Platform.Persistence;

public interface IRuntimeDatabaseConnectionFactory
{
    IModuleDatabaseConnectionFactory ForModule(PersistenceModuleIdentity module);
}

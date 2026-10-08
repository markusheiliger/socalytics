namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal sealed class MigrationCatalogException : Exception
{
    public MigrationCatalogException(string message)
        : base(message)
    {
    }
}

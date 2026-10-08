namespace SocAlytics.Platform.Migrator;

internal enum MigratorExitCode
{
    Success = 0,
    UnexpectedError = 1,
    ConfigurationInvalid = 2,
    DatabaseUnavailable = 3,
    CatalogInvalid = 4,
    ChecksumMismatch = 5,
    SequenceConflict = 6,
    MigrationFailed = 7,
    LockTimeout = 8,
    Cancelled = 9,
}

namespace SocAlytics.Platform.Persistence;

public sealed class MigrationException : Exception
{
    internal MigrationException(string message) : base(message)
    {
    }
}

namespace SocAlytics.Platform.Persistence;

/// <summary>A migration failure whose message identifies only the module and script, never connection details.</summary>
public sealed class MigrationException(string message) : Exception(message);

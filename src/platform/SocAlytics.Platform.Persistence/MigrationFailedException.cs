namespace SocAlytics.Platform.Persistence;

/// <summary>A migration failure carrying only the module, script identity, and a sanitized reason.</summary>
public sealed class MigrationFailedException(string message) : Exception(message);

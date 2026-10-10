namespace SocAlytics.Platform.Application.Abstractions.Persistence;

/// <summary>A write violated a unique constraint; the surrounding unit of work must be rolled back.</summary>
public sealed class UniqueViolationException(string constraintName, Exception? inner = null)
    : Exception($"A unique constraint was violated: {constraintName}.", inner)
{
    public string ConstraintName { get; } = constraintName;
}

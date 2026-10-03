namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Standardizes the affected-row check for module-owned SQL of the form
/// <c>UPDATE ... SET version = version + 1 WHERE id = @id AND version = @expected</c>.
/// </summary>
public static class OptimisticConcurrency
{
    /// <summary>Returns true when exactly one row matched the expected version.</summary>
    public static bool IsApplied(int affectedRows) => affectedRows switch
    {
        1 => true,
        0 => false,
        _ => throw new InvalidOperationException("A version-guarded write must affect at most one row."),
    };

    /// <summary>Throws <see cref="ConcurrencyConflictException"/> when the expected version did not match.</summary>
    public static void EnsureApplied(int affectedRows)
    {
        if (!IsApplied(affectedRows))
        {
            throw new ConcurrencyConflictException();
        }
    }
}

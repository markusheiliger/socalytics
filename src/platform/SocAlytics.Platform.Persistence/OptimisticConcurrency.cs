namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Standardizes the affected-row check for module-owned versioned updates of the form
/// <c>UPDATE ... SET ..., version = version + 1 WHERE id = @id AND version = @expected</c>.
/// </summary>
public static class OptimisticConcurrency
{
    /// <summary>
    /// Requires exactly one affected row. Zero rows is reported as a <see cref="ConcurrencyConflictException"/>;
    /// the caller's transaction then rolls back, so nothing from the contested change is committed.
    /// </summary>
    public static void EnsureUpdated(int affectedRows)
    {
        switch (affectedRows)
        {
            case 1:
                return;
            case 0:
                throw new ConcurrencyConflictException();
            default:
                throw new InvalidOperationException(
                    $"A versioned update must affect exactly one row but affected {affectedRows}.");
        }
    }
}

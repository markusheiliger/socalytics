namespace SocAlytics.Platform.Persistence;

/// <summary>
/// Validates the affected-row count of a module-owned update of the form
/// <c>UPDATE ... SET version = version + 1 WHERE id = @id AND version = @expectedVersion</c>.
/// </summary>
public static class OptimisticConcurrency
{
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

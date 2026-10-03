namespace SocAlytics.Platform.Persistence;

public sealed class PersistenceOptions
{
    /// <summary>
    /// Connection string of the privileged bootstrap login. It is used only by migration
    /// orchestration and by role-scoped runtime sessions; it is never exposed to modules.
    /// </summary>
    public string? ConnectionString { get; set; }

    public override string ToString() => nameof(PersistenceOptions);
}

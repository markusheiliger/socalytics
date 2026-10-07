namespace SocAlytics.Platform.Migrator;

internal sealed class MigratorOptions
{
    public const string SectionName = "Migrator";

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public bool IsValid() =>
        ConnectTimeout > TimeSpan.Zero && LockWaitTimeout > TimeSpan.Zero && ScriptTimeout > TimeSpan.Zero;
}

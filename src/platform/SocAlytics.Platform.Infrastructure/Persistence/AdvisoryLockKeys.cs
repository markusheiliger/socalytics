namespace SocAlytics.Platform.Infrastructure.Persistence;

// Distinct from the Migrator's migration lock (5459779, 1).
internal static class AdvisoryLockKeys
{
    public const long ClubBootstrap = 0x536F634C_42535401;

    public const long ClubAdminInvariant = 0x536F634C_41444D02;
}

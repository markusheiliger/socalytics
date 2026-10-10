namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record TeamVisibility(bool AllTeams, IReadOnlySet<Guid> TeamIds);

using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record TeamScope(Guid TeamId, Guid SeasonId, SeasonState SeasonState);

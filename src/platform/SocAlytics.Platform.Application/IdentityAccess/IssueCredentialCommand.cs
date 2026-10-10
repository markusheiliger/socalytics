using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record IssueCredentialCommand(Guid MemberId, CredentialPurpose Purpose);

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed record ListMembersQuery(string? PageSize, string? ContinuationToken);

public sealed record MemberPage(IReadOnlyList<MemberDetails> Items, string? ContinuationToken);

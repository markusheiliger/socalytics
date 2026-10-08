namespace SocAlytics.Platform.Application.IdentityAccess;

public enum AccessDecisionKind
{
    Granted,
    NotFound,
    Forbidden,
}

public sealed record AccessDecision(AccessDecisionKind Kind, TeamScope? Scope)
{
    public bool IsGranted => Kind == AccessDecisionKind.Granted;

    public static AccessDecision Granted(TeamScope? scope = null) => new(AccessDecisionKind.Granted, scope);

    public static AccessDecision NotFound { get; } = new(AccessDecisionKind.NotFound, null);

    public static AccessDecision Forbidden { get; } = new(AccessDecisionKind.Forbidden, null);
}

namespace SocAlytics.Platform.Application.IdentityAccess;

public enum AccessDecisionKind
{
    Granted,
    NotFound,
    Forbidden,
}

public sealed record AccessDecision(AccessDecisionKind Kind, TeamScope? Scope, bool ResourceExists = false)
{
    public bool IsGranted => Kind == AccessDecisionKind.Granted;

    /// <summary>True when the resource exists but the caller lacks authority, so callers that report 403 must not report 404.</summary>
    public bool IsForbidden => Kind == AccessDecisionKind.Forbidden || ResourceExists;

    /// <summary>Not found for a resource that exists but is outside the caller's Teams.</summary>
    public static AccessDecision NotVisible { get; } = new(AccessDecisionKind.NotFound, null, true);

    public static AccessDecision Granted(TeamScope? scope = null) => new(AccessDecisionKind.Granted, scope);

    public static AccessDecision NotFound { get; } = new(AccessDecisionKind.NotFound, null);

    public static AccessDecision Forbidden { get; } = new(AccessDecisionKind.Forbidden, null);
}

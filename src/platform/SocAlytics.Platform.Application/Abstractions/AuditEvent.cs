namespace SocAlytics.Platform.Application.Abstractions;

public enum AuditOutcome
{
    Succeeded,
    Unchanged,
    Denied,
    Failed,
    Refused,
}

public static class AuditOutcomeExtensions
{
    public static string ToWireValue(this AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Succeeded => "succeeded",
        AuditOutcome.Unchanged => "unchanged",
        AuditOutcome.Denied => "denied",
        AuditOutcome.Failed => "failed",
        AuditOutcome.Refused => "refused",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

/// <summary>Resource types: club, season, team, match, member, session, credential, upload-session, recording-version, timeline-mapping, recording-set-version.</summary>
public sealed record AuditResource(string Type, string? Id);

public sealed record AuditActorOverride(AuditActorKind Kind, Guid? AccountId);

public sealed record AuditEvent
{
    public required string EventType { get; init; }

    public required string Action { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required AuditResource Resource { get; init; }

    public Guid? TeamId { get; init; }

    public string? ReasonCode { get; init; }

    public IReadOnlyDictionary<string, string>? Details { get; init; }

    public AuditActorOverride? ActorOverride { get; init; }
}

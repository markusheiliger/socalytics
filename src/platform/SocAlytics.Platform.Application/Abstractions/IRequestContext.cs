namespace SocAlytics.Platform.Application.Abstractions;

public enum AuditActorKind
{
    Member,
    System,
    Anonymous,
}

public interface IRequestContext
{
    Guid? MemberAccountId { get; }

    Guid? SessionId { get; }

    string CorrelationId { get; }

    AuditActorKind ActorKind { get; }
}

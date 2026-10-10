using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

public sealed class TestRequestContext : IRequestContext
{
    public Guid? MemberAccountId { get; set; }

    public Guid? SessionId { get; set; }

    public string CorrelationId { get; set; } = "test-correlation";

    public AuditActorKind ActorKind { get; set; } = AuditActorKind.System;
}

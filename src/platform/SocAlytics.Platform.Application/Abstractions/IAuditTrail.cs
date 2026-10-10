namespace SocAlytics.Platform.Application.Abstractions;

public interface IAuditTrail
{
    /// <summary>Records the event on the current unit of work.</summary>
    Task RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken);

    /// <summary>Records the event in its own transaction.</summary>
    Task RecordIndependentAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
}

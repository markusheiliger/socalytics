using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Domain.Registry;

namespace SocAlytics.Platform.Application.Registry.Decisions;

public sealed class RejectRegistrationHandler(
    IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IRegistrationStore store, IAuditTrail audit,
    IRequestContext context, TimeProvider time)
{
    public Task<OperationResult<AnalystManagerRegistration>> HandleAsync(
        Guid registrationId, string? reason, CancellationToken cancellationToken) =>
        RegistrationDecisionSupport.DecideAsync(
            registrationId, reason, true, unitOfWork, authorizer, store, audit, context, time, cancellationToken);
}

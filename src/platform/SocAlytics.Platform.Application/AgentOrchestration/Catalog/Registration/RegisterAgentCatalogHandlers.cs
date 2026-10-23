using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog.Registration;

public sealed record RegisterAgentCatalogArtifactCommand(AgentCatalogRecordKind Kind, JsonElement Document);

public sealed class RegisterAgentCatalogArtifactHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    AgentCatalogRegistrar registrar,
    ProfileDistanceCalculator distances,
    IRequestContext context)
{
    public async Task<RegisterCatalogResult<AgentCatalogArtifactRecord>> HandleAsync(
        RegisterAgentCatalogArtifactCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "register-agent-catalog-record", new AuditResource(AgentCatalogAuditEvents.ArtifactResourceType, null), cancellationToken))
        {
            return RegisterCatalogResult<AgentCatalogArtifactRecord>.Failed(OperationFailure.Forbidden());
        }

        if (context.MemberAccountId is not { } memberId)
        {
            return RegisterCatalogResult<AgentCatalogArtifactRecord>.Failed(OperationFailure.Unauthenticated());
        }

        var result = await registrar.RegisterArtifactAsync(command.Kind, command.Document, memberId.ToString(), false, cancellationToken);
        if (result.IsRegistered)
        {
            if (command.Kind == AgentCatalogRecordKind.PhilosophyProfile)
            {
                var record = result.Record!;
                result.WithDistances(await distances.ComputeAsync(record.Id, record.Version, record.Document, null, null, cancellationToken));
            }

            await scope.CommitAsync(cancellationToken);
        }

        return result;
    }
}

public sealed record RegisterAgentVersionCommand(string? Version, JsonElement Document);

public sealed class RegisterAgentVersionHandler(
    IUnitOfWork unitOfWork,
    IAccessAuthorizer authorizer,
    IAuditTrail audit,
    AgentCatalogRegistrar registrar,
    IRequestContext context)
{
    public async Task<RegisterCatalogResult<AgentVersionRecord>> HandleAsync(
        RegisterAgentVersionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (!await AgentCatalogAccess.AuthorizeAsync(
                authorizer, audit, "register-agent-catalog-record", new AuditResource(AgentCatalogAuditEvents.AgentVersionResourceType, null), cancellationToken))
        {
            return RegisterCatalogResult<AgentVersionRecord>.Failed(OperationFailure.Forbidden());
        }

        if (context.MemberAccountId is not { } memberId)
        {
            return RegisterCatalogResult<AgentVersionRecord>.Failed(OperationFailure.Unauthenticated());
        }

        var result = await registrar.RegisterAgentVersionAsync(command.Version, command.Document, memberId.ToString(), false, cancellationToken);
        if (result.IsRegistered)
        {
            await scope.CommitAsync(cancellationToken);
        }

        return result;
    }
}

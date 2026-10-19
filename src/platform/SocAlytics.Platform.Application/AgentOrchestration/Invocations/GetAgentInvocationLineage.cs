using System.Text.Json;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.AgentOrchestration;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record GetAgentInvocationLineageQuery(Guid InvocationId);

// Details carry only the bounded payload of an entry (checkpoint kind and note, failure reason and retryability,
// declared execution references); the attempt is reported through AttemptId and AttemptNumber.
public sealed record AgentLineageEntryView(
    int Sequence,
    string Kind,
    Guid? AttemptId,
    int? AttemptNumber,
    DateTimeOffset RecordedAt,
    IReadOnlyDictionary<string, JsonElement> Details);

public sealed record AgentInvocationLineageView(
    AgentInvocationRecord Invocation,
    IReadOnlyList<AgentLineageEntryView> Entries,
    AgentAcceptedAdviceRecord? Advice);

public sealed class GetAgentInvocationLineageHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext)
{
    private static readonly HashSet<string> AttemptKeys = new(StringComparer.Ordinal) { "attemptId", "attemptNumber" };

    public async Task<OperationResult<AgentInvocationLineageView>> HandleAsync(
        GetAgentInvocationLineageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.NotFound();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.InvocationResourceType, query.InvocationId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var invocation = await invocations.FindAsync(query.InvocationId, cancellationToken);
        if (invocation is null || invocation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        var lineage = await invocations.ReadLineageAsync(invocation.Id, cancellationToken);

        var attemptNumbers = new Dictionary<Guid, int>();
        foreach (var attemptId in lineage.Entries.Where(entry => entry.AttemptId is not null).Select(entry => entry.AttemptId!.Value).Distinct())
        {
            var attempt = await attempts.FindAsync(attemptId, cancellationToken);
            if (attempt is not null)
            {
                attemptNumbers[attemptId] = attempt.AttemptNumber;
            }
        }

        var entries = lineage.Entries
            .OrderBy(entry => entry.Sequence)
            .Select(entry => new AgentLineageEntryView(
                entry.Sequence,
                entry.Kind,
                entry.AttemptId,
                entry.AttemptId is { } id && attemptNumbers.TryGetValue(id, out var number) ? number : null,
                entry.RecordedAt,
                ReadDetails(entry.PayloadJson)))
            .ToList();

        return new AgentInvocationLineageView(invocation, entries, lineage.Advice);
    }

    private static Dictionary<string, JsonElement> ReadDetails(string payloadJson)
    {
        var details = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!AttemptKeys.Contains(property.Name))
                {
                    details[property.Name] = property.Value.Clone();
                }
            }
        }

        return details;
    }
}

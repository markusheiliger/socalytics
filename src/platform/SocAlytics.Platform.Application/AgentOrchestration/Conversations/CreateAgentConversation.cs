using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

public sealed record CreateAgentConversationCommand(Guid MatchId, string? IdempotencyKey, Guid? AnalysisRunId);

public sealed record CreateAgentConversationResult(AgentConversationView Conversation, bool Replayed);

public sealed class CreateAgentConversationHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAnalysisRunStore runs,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task<OperationResult<CreateAgentConversationResult>> HandleAsync(
        CreateAgentConversationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.Unauthenticated();
        }

        var key = command.IdempotencyKey;
        OperationFailure? invalid = null;
        if (string.IsNullOrEmpty(key))
        {
            invalid = OperationFailure.Validation("idempotency-key-missing", new FieldViolation("Idempotency-Key", "required"));
        }
        else if (key.Length > 255 || key.Any(c => c is < '\u0021' or > '\u007e'))
        {
            invalid = OperationFailure.Validation(new FieldViolation("Idempotency-Key", "invalid"));
        }
        else if (command.AnalysisRunId is not { } given || given == Guid.Empty)
        {
            invalid = OperationFailure.Validation(new FieldViolation("analysisRunId", "required"));
        }

        var runId = command.AnalysisRunId ?? Guid.Empty;
        var hash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"create-conversation\n{command.MatchId:D}\n{runId:D}")));

        // A concurrent request with the same key can win the insert; the second pass then replays its outcome.
        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                return await TryAsync(command.MatchId, runId, key ?? string.Empty, hash, ownerId, invalid, cancellationToken);
            }
            catch (UniqueViolationException) when (pass == 0)
            {
            }
        }

        throw new InvalidOperationException("The conversation could not be created or replayed.");
    }

    private async Task<OperationResult<CreateAgentConversationResult>> TryAsync(
        Guid matchId, Guid runId, string key, string hash, Guid ownerId, OperationFailure? invalid, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", matchId), TeamPermission.Write, cancellationToken);
        if (!decision.IsGranted)
        {
            return decision.IsForbidden ? OperationFailure.Forbidden() : OperationFailure.NotFound();
        }

        if (invalid is not null)
        {
            return invalid;
        }

        var existing = await conversations.FindByOwnerAndKeyAsync(ownerId, key, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                return OperationFailure.Conflict("idempotency-key-reused");
            }

            return new CreateAgentConversationResult(await ToViewAsync(existing, cancellationToken), true);
        }

        var run = await runs.LockRunAsync(runId, cancellationToken);
        if (run is null
            || run.MatchId != matchId
            || run.State is not (AnalysisRunState.Completed or AnalysisRunState.PartiallyCompleted))
        {
            return OperationFailure.Conflict("analysis-run-not-eligible");
        }

        var teamId = decision.Scope!.TeamId;
        var conversationId = Guid.CreateVersion7();
        await conversations.InsertAsync(
            new NewAgentConversation(conversationId, teamId, matchId, runId, ownerId, key, hash, time.GetUtcNow(), []),
            cancellationToken);
        await audit.RecordAsync(AgentOrchestrationAuditEvents.ConversationCreated(conversationId, teamId), cancellationToken);

        var created = await conversations.LockByIdAsync(conversationId, cancellationToken)
            ?? throw new InvalidOperationException("The inserted conversation could not be read.");
        var view = await ToViewAsync(created, cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new CreateAgentConversationResult(view, false);
    }

    private async Task<AgentConversationView> ToViewAsync(AgentConversationRecord record, CancellationToken cancellationToken) =>
        new(record, await conversations.IsCurrentRunAsync(record.MatchId, record.AnalysisRunId, cancellationToken));
}

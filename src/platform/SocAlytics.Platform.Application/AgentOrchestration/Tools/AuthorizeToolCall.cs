using System.Text.Json;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record AuthorizeToolCallCommand(
    Guid AttemptId,
    string WorkloadClientId,
    string FencingToken,
    string ToolId,
    string CallId,
    string ArgumentsDigest,
    bool IsClipResolution);

/// <summary>An authorized, reserved call: the bound scope the read runs against.</summary>
public sealed record AuthorizedToolCall(
    Guid AttemptId,
    Guid InvocationId,
    Guid TeamId,
    Guid MatchId,
    Guid AnalysisRunId,
    Guid InitiatingMemberId,
    string ToolId,
    string CallId,
    string ArgumentsDigest);

public static class AgentToolCodes
{
    public const string NotPermitted = "agent-tool-not-permitted";
    public const string CallIdReused = "agent-tool-call-id-reused";
    public const string LimitReached = "agent-tool-call-limit-reached";
    public const string LimitExtension = "limit";
    public const string CallsLimit = "calls";
    public const string ClipResolutionsLimit = "clip-resolutions";
}

/// <summary>
/// Binds a tool call to the invocation's current attempt, re-evaluates the initiating member, checks the pinned tool policy,
/// the call identifier, and the per-attempt limits, and reserves the call, in one unit of work under the invocation lock.
/// </summary>
public sealed class AuthorizeToolCallHandler(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IAgentAttemptStore attempts,
    IAgentInvocationPinStore pins,
    IAgentCatalogArtifactStore artifacts,
    IAgentToolCallStore toolCalls,
    IInvocationScopeEvaluator scopes,
    IAuditTrail audit,
    IOptionsMonitor<AgentToolOptions> options,
    TimeProvider time)
{
    public async Task<OperationResult<AuthorizedToolCall>> HandleAsync(AuthorizeToolCallCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // The invocation is locked before the attempt, in the same order as the claim.
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var unlocked = await attempts.FindAsync(command.AttemptId, cancellationToken);
        if (unlocked is null || unlocked.WorkloadClientId != command.WorkloadClientId)
        {
            return OperationFailure.NotFound();
        }

        var invocation = await invocations.LockAsync(unlocked.InvocationId, cancellationToken);
        var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
        if (invocation is null || attempt is null)
        {
            return OperationFailure.NotFound();
        }

        var now = time.GetUtcNow();
        if (AgentAttemptGuard.StaleDisposition(invocation, attempt, command.FencingToken, now) is { } disposition)
        {
            await audit.RecordAsync(
                AgentToolAuditEvents.AttemptStaleRefused(
                    attempt.Id, invocation.Id, invocation.TeamId, command.ToolId, command.CallId,
                    AgentPersistedNames.ToPersistedName(disposition)),
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return AgentExecutionSupport.Stale(disposition);
        }

        if (await AgentScopeLoss.EnforceAsync(scopes, invocations, attempts, audit, invocation, attempt, now, cancellationToken))
        {
            await AppendAsync(invocation, attempt, command, ToolCallOutcome.ScopeLost, null, now, cancellationToken);
            await audit.RecordAsync(
                AgentToolAuditEvents.ScopeLost(
                    attempt.Id, invocation.Id, invocation.TeamId, invocation.OwnerMemberId, command.ToolId, command.CallId),
                cancellationToken);
            AgentToolMetrics.CallRecorded(command.ToolId, ToolCallNames.ToWireName(ToolCallOutcome.ScopeLost));
            await scope.CommitAsync(cancellationToken);
            return AgentExecutionSupport.Stale(AgentStaleDisposition.ScopeLost);
        }

        if (!await IsPermittedAsync(invocation, command.ToolId, cancellationToken))
        {
            await AppendAsync(invocation, attempt, command, ToolCallOutcome.NotPermitted, null, now, cancellationToken);
            await audit.RecordAsync(
                AgentToolAuditEvents.NotPermittedRefused(attempt.Id, invocation.Id, invocation.TeamId, command.ToolId, command.CallId),
                cancellationToken);
            AgentToolMetrics.CallRecorded(command.ToolId, ToolCallNames.ToWireName(ToolCallOutcome.NotPermitted));
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Forbidden(AgentToolCodes.NotPermitted);
        }

        var limit = await ExhaustedLimitAsync(command, cancellationToken);
        if (limit is not null)
        {
            await AppendAsync(invocation, attempt, command, ToolCallOutcome.LimitReached, limit, now, cancellationToken);
            await audit.RecordAsync(
                AgentToolAuditEvents.LimitReachedRefused(
                    attempt.Id, invocation.Id, invocation.TeamId, command.ToolId, command.CallId, limit),
                cancellationToken);
            AgentToolMetrics.CallRecorded(command.ToolId, ToolCallNames.ToWireName(ToolCallOutcome.LimitReached));
            await scope.CommitAsync(cancellationToken);
            return new OperationFailure(
                OperationFailureKind.TooManyRequests, AgentToolCodes.LimitReached, [], null,
                new Dictionary<string, string> { [AgentToolCodes.LimitExtension] = limit });
        }

        var reserved = await toolCalls.TryReserveAsync(
            new NewAgentToolCall(
                attempt.Id, command.CallId, invocation.Id, command.ToolId, command.ArgumentsDigest, command.IsClipResolution, now),
            cancellationToken);
        if (!reserved)
        {
            await audit.RecordAsync(
                AgentToolAuditEvents.CallIdReusedRefused(attempt.Id, invocation.Id, invocation.TeamId, command.ToolId, command.CallId),
                cancellationToken);
            await scope.CommitAsync(cancellationToken);
            return OperationFailure.Conflict(AgentToolCodes.CallIdReused);
        }

        await scope.CommitAsync(cancellationToken);
        return new AuthorizedToolCall(
            attempt.Id, invocation.Id, invocation.TeamId, invocation.MatchId, invocation.AnalysisRunId, invocation.OwnerMemberId,
            command.ToolId, command.CallId, command.ArgumentsDigest);
    }

    private async Task<string?> ExhaustedLimitAsync(AuthorizeToolCallCommand command, CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var counts = await toolCalls.CountAsync(command.AttemptId, cancellationToken);
        if (counts.Calls >= current.MaxCallsPerAttempt)
        {
            return AgentToolCodes.CallsLimit;
        }

        return command.IsClipResolution && counts.ClipResolutions >= current.MaxClipResolutionsPerAttempt
            ? AgentToolCodes.ClipResolutionsLimit
            : null;
    }

    // The pinned tool policy decides; an invocation without pins permits nothing.
    private async Task<bool> IsPermittedAsync(AgentInvocationRecord invocation, string toolId, CancellationToken cancellationToken)
    {
        var pin = (await pins.ListAsync(invocation.Id, cancellationToken)).FirstOrDefault(p => p.Kind == AgentCatalogRecordKind.ToolPolicy);
        if (pin is null)
        {
            return false;
        }

        var artifact = await artifacts.FindAsync(pin.Kind, pin.ArtifactId, pin.ArtifactVersion, cancellationToken);
        if (artifact is null)
        {
            return false;
        }

        using var document = JsonDocument.Parse(artifact.Document);
        return document.RootElement.TryGetProperty("tools", out var tools)
            && tools.ValueKind == JsonValueKind.Array
            && tools.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == toolId);
    }

    private Task AppendAsync(
        AgentInvocationRecord invocation, AgentAttemptRecord attempt, AuthorizeToolCallCommand command, ToolCallOutcome outcome,
        string? reasonCode, DateTimeOffset now, CancellationToken cancellationToken) =>
        ToolCallLineage.AppendAsync(
            invocations,
            new ToolCallLineageEntry(
                invocation.Id, attempt.Id, command.CallId, command.ToolId, outcome, null, reasonCode, command.ArgumentsDigest,
                null, [], [], AgentToolAllowlist.Entries.FirstOrDefault(e => e.ToolId == command.ToolId)?.ProducingCapability, now),
            cancellationToken);
}

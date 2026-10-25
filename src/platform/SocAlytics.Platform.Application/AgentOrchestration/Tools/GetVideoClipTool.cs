using System.Globalization;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Evidence;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;
using SocAlytics.Platform.Domain.Analysis.Evidence;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record GetVideoClipToolCommand(
    AuthorizedToolCall Call, EvidenceFactRequest? Fact, EvidenceRangeRequest? Range, bool RetryFailed);

/// <summary>The wire result of the call; it carries links only in <c>parts[].media.url</c> and must never be stored or logged.</summary>
public sealed record GetVideoClipToolResult(JsonObject Result);

/// <summary>
/// <c>get_video_clip</c>: resolves a fact or range of the bound run through the shared evidence media core with the workload
/// as audit actor, and appends the tool-call lineage entry with media references and a link-free result digest.
/// </summary>
public sealed class GetVideoClipTool(
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IEvidenceMediaResolver resolver,
    IAuditTrail audit,
    TimeProvider time)
{
    public const string ToolId = "get_video_clip";
    public const string StorageUnavailableCode = ResolveEvidenceMediaHandler.StorageUnavailableCode;

    private static readonly AuditActorOverride WorkloadActor = new(AuditActorKind.AgentWorkload, null);

    public async Task<OperationResult<GetVideoClipToolResult>> HandleAsync(
        GetVideoClipToolCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var call = command.Call;
        var resolution = new ResolveEvidenceMediaCommand(call.AnalysisRunId, command.Fact, command.Range, command.RetryFailed);

        var violations = resolver.Validate(resolution);
        if (violations.Count > 0)
        {
            await AppendAsync(call, ToolCallOutcome.Invalid, null, null, [], [], cancellationToken);
            return OperationFailure.Validation([.. violations]);
        }

        EvidenceMediaResolved resolved;
        JsonObject result;
        try
        {
            await using var scope = await unitOfWork.BeginAsync(cancellationToken);
            if (await invocations.LockAsync(call.InvocationId, cancellationToken) is null)
            {
                return OperationFailure.NotFound();
            }

            var resolveResult = await resolver.ResolveAsync(
                new EvidenceRunScope(call.AnalysisRunId), resolution, WorkloadActor, cancellationToken);
            if (!resolveResult.IsSuccess)
            {
                var failure = resolveResult.Failure;
                if (failure.Kind == OperationFailureKind.NotFound)
                {
                    await AppendInScopeAsync(call, ToolCallOutcome.NotFound, null, null, [], [], cancellationToken);
                    AgentToolMetrics.CallRecorded(ToolId, ToolCallNames.ToWireName(ToolCallOutcome.NotFound));
                    await scope.CommitAsync(cancellationToken);
                }

                return failure;
            }

            resolved = resolveResult.Value;
            result = ToResult(resolved.Resolution);
            var media = resolved.Resolution.Parts
                .Where(p => p.Media is not null)
                .Select(p => new ToolCallMediaEvidence(p.Media!.MediaDigest, p.StartMs, p.EndMs))
                .ToList();
            IReadOnlyList<ToolCallFactEvidence> facts = command.Fact is { AcceptedResultId: { } id, Ordinal: { } ordinal }
                ? [new ToolCallFactEvidence(id, ordinal)]
                : [];

            if (media.Count > 0)
            {
                var granted = resolved.Resolution.Parts.Where(p => p.Media is not null).Select(p => p.Media!).ToList();
                await audit.RecordAsync(
                    AgentToolAuditEvents.EvidenceMediaGranted(
                        call.AttemptId, call.InvocationId, call.TeamId, call.InitiatingMemberId, ToolId, call.CallId,
                        [.. granted.Select(m => m.MediaDigest).Distinct(StringComparer.Ordinal)],
                        granted.Max(m => m.ExpiresAt)),
                    cancellationToken);
            }

            await AppendInScopeAsync(
                call, ToolCallOutcome.Answered, null, ToolCallLineage.ResultDigest(result), facts, media, cancellationToken);
            AgentToolMetrics.CallRecorded(ToolId, ToolCallNames.ToWireName(ToolCallOutcome.Answered));
            await scope.CommitAsync(cancellationToken);
        }
        catch (ObjectStorageUnavailableException)
        {
            await AppendAsync(
                call, ToolCallOutcome.Failed, ToolCallReason.EvidenceStorageUnavailable, null, [], [], cancellationToken);
            return OperationFailure.Unavailable(StorageUnavailableCode);
        }

        await resolver.InvalidateAsync(resolved.Invalidated, cancellationToken);
        return new GetVideoClipToolResult(result);
    }

    // Records an outcome in its own unit of work, for calls that end before or without a committed resolution.
    private async Task AppendAsync(
        AuthorizedToolCall call, ToolCallOutcome outcome, ToolCallReason? reason, string? resultDigest,
        IReadOnlyList<ToolCallFactEvidence> facts, IReadOnlyList<ToolCallMediaEvidence> media, CancellationToken cancellationToken)
    {
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (await invocations.LockAsync(call.InvocationId, cancellationToken) is null)
        {
            return;
        }

        await AppendInScopeAsync(call, outcome, reason, resultDigest, facts, media, cancellationToken);
        AgentToolMetrics.CallRecorded(ToolId, ToolCallNames.ToWireName(outcome));
        await scope.CommitAsync(cancellationToken);
    }

    private Task AppendInScopeAsync(
        AuthorizedToolCall call, ToolCallOutcome outcome, ToolCallReason? reason, string? resultDigest,
        IReadOnlyList<ToolCallFactEvidence> facts, IReadOnlyList<ToolCallMediaEvidence> media, CancellationToken cancellationToken) =>
        ToolCallLineage.AppendAsync(
            invocations,
            new ToolCallLineageEntry(
                call.InvocationId, call.AttemptId, call.CallId, call.ToolId, outcome, reason, null, call.ArgumentsDigest,
                resultDigest, facts, media, null, time.GetUtcNow()),
            cancellationToken);

    private static JsonObject ToResult(EvidenceResolution r)
    {
        var parts = new JsonArray();
        foreach (var part in r.Parts)
        {
            var node = new JsonObject
            {
                ["startMs"] = part.StartMs,
                ["endMs"] = part.EndMs,
                ["state"] = part.State.ToWireName(),
            };
            if (part.Reason is not null)
            {
                node["reason"] = part.Reason;
            }

            if (part.RetryAfterSeconds is { } retryAfter)
            {
                node["retryAfterSeconds"] = retryAfter;
            }

            if (part.Media is { } m)
            {
                node["media"] = new JsonObject
                {
                    ["url"] = m.Url.AbsoluteUri,
                    ["expiresAt"] = m.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                    ["mediaType"] = m.MediaType,
                    ["mediaDigest"] = m.MediaDigest,
                    ["mediaSizeBytes"] = m.MediaSizeBytes,
                    ["startOffsetMs"] = m.StartOffsetMs,
                    ["endOffsetMs"] = m.EndOffsetMs,
                    ["recordingVersionId"] = m.RecordingVersionId.ToString(),
                };
            }

            parts.Add(node);
        }

        var result = new JsonObject
        {
            ["run"] = new JsonObject
            {
                ["runId"] = r.RunId.ToString(),
                ["matchId"] = r.MatchId.ToString(),
                ["teamId"] = r.TeamId.ToString(),
            },
            ["requested"] = new JsonObject { ["fromMs"] = r.RequestedFromMs, ["toMs"] = r.RequestedToMs },
        };
        if (r.FactStartMs is { } start && r.FactEndMs is { } end)
        {
            result["fact"] = new JsonObject { ["startMs"] = start, ["endMs"] = end };
        }

        result["shortened"] = r.Shortened;
        result["parts"] = parts;
        return result;
    }
}

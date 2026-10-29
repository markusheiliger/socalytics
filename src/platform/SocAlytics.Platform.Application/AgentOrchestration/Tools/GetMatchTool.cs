using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record GetMatchToolCommand(
    Guid AttemptId,
    string WorkloadClientId,
    string FencingToken,
    string CallId,
    JsonNode? Arguments);

/// <summary>The answer of a tool call that was reserved and read; <see cref="Result"/> is null unless the outcome is answered.</summary>
public sealed record ToolCallAnswer(ToolCallOutcome Outcome, ToolCallReason? Reason, JsonNode? Result);

/// <summary>
/// <c>get_match</c>: the match summary, the bound run, and the fact streams of the invocation's bound run, with an optional fact page.
/// Reads only the run the invocation is bound to, through the same cores as the member reads.
/// </summary>
public sealed class GetMatchToolHandler(
    AuthorizeToolCallHandler authorize,
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    IClubHierarchyStore clubs,
    IRunSupersessionStore supersessions,
    IRunFactStreamReader streamReader,
    IRunFactReader factReader,
    TimeProvider time)
{
    public const string ToolId = "get_match";

    public async Task<OperationResult<ToolCallAnswer>> HandleAsync(GetMatchToolCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authorized = await authorize.HandleAsync(
            new AuthorizeToolCallCommand(
                command.AttemptId, command.WorkloadClientId, command.FencingToken, ToolId, command.CallId,
                ToolCallLineage.ArgumentsDigest(command.Arguments), false),
            cancellationToken);
        if (!authorized.IsSuccess)
        {
            return authorized.Failure;
        }

        var call = authorized.Value;
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);

        // The invocation lock serializes the lineage append with the monitor and the scope-loss transition.
        if (await invocations.LockAsync(call.InvocationId, cancellationToken) is null)
        {
            return OperationFailure.NotFound();
        }

        var outcome = await ReadAsync(call, command.Arguments, cancellationToken);
        var (toolOutcome, reason, result, facts, failure) = outcome;

        var entry = new ToolCallLineageEntry(
            call.InvocationId, call.AttemptId, call.CallId, ToolId, toolOutcome, reason, null, call.ArgumentsDigest,
            result is null ? null : ToolCallLineage.ResultDigest(result), facts, [], null, time.GetUtcNow());
        await ToolCallLineage.AppendAsync(invocations, entry, cancellationToken);
        AgentToolMetrics.CallRecorded(ToolId, ToolCallNames.ToWireName(toolOutcome));
        await scope.CommitAsync(cancellationToken);

        return failure is not null ? failure : new ToolCallAnswer(toolOutcome, reason, result);
    }

    private sealed record ReadOutcome(
        ToolCallOutcome Outcome,
        ToolCallReason? Reason,
        JsonNode? Result,
        IReadOnlyList<ToolCallFactEvidence> Facts,
        OperationFailure? Failure);

    private static ReadOutcome Refused(ToolCallOutcome outcome, OperationFailure failure) => new(outcome, null, null, [], failure);

    private async Task<ReadOutcome> ReadAsync(AuthorizedToolCall call, JsonNode? arguments, CancellationToken cancellationToken)
    {
        ValidatedFactSelection? selection = null;
        if (!TryParseFacts(call.AnalysisRunId, arguments, out var query, out var argumentFailure))
        {
            return Refused(ToolCallOutcome.Invalid, argumentFailure!);
        }

        if (query is not null)
        {
            var validated = factReader.Validate(query);
            if (!validated.IsSuccess)
            {
                return Refused(ToolCallOutcome.Invalid, validated.Failure);
            }

            selection = validated.Value;
        }

        var match = await clubs.GetMatchAsync(call.MatchId, cancellationToken);
        if (match is null)
        {
            return Refused(ToolCallOutcome.NotFound, OperationFailure.NotFound());
        }

        var runScope = new AuthorizedRunScope(call.AnalysisRunId);
        var streams = await streamReader.ReadAsync(runScope, cancellationToken);
        if (!streams.IsSuccess)
        {
            return Refused(ToolCallOutcome.NotFound, streams.Failure);
        }

        var result = new JsonObject
        {
            ["match"] = MatchJson(match),
            ["run"] = await RunJsonAsync(call, streams.Value.Run.State, cancellationToken),
            ["streams"] = new JsonArray([.. streams.Value.Streams.Select(e => (JsonNode)StreamEntryJson(e, streams.Value.Run))]),
        };

        var facts = new List<ToolCallFactEvidence>();
        if (selection is not null)
        {
            var page = await factReader.ReadAsync(runScope, selection, cancellationToken);
            if (!page.IsSuccess)
            {
                if (page.Failure.Kind == OperationFailureKind.NotFound && page.Failure.Code == ListRunFactsHandler.CapabilityNotInRun)
                {
                    return new ReadOutcome(ToolCallOutcome.Unavailable, ToolCallReason.CapabilityNotInRun, null, [], null);
                }

                return page.Failure.Kind == OperationFailureKind.Validation
                    ? Refused(ToolCallOutcome.Invalid, page.Failure)
                    : Refused(ToolCallOutcome.NotFound, page.Failure);
            }

            result["facts"] = FactsJson(page.Value);
            facts.AddRange(page.Value.Items.Select(i => new ToolCallFactEvidence(i.AcceptedResultId, i.Ordinal)));
        }

        return new ReadOutcome(ToolCallOutcome.Answered, null, result, facts, null);
    }

    private static bool TryParseFacts(Guid runId, JsonNode? arguments, out ListRunFactsQuery? query, out OperationFailure? failure)
    {
        query = null;
        failure = null;
        if (arguments is null)
        {
            return true;
        }

        if (arguments is not JsonObject root || root.Any(p => p.Key != "facts"))
        {
            failure = OperationFailure.Validation(new FieldViolation("arguments", "invalid"));
            return false;
        }

        if (root["facts"] is null)
        {
            return true;
        }

        if (root["facts"] is not JsonObject facts)
        {
            failure = OperationFailure.Validation(new FieldViolation("facts", "invalid"));
            return false;
        }

        var violations = new List<FieldViolation>();
        var known = new[]
        {
            "capabilityId", "capabilityVersion", "schemaVersion", "nodeKey", "fromMs", "toMs", "pageSize", "continuationToken",
        };
        violations.AddRange(facts.Where(p => !known.Contains(p.Key)).Select(p => new FieldViolation($"facts.{p.Key}", "unknown")));

        string? Text(string name)
        {
            switch (facts[name])
            {
                case null:
                    return null;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return text;
                case JsonValue value when value.TryGetValue<long>(out var number):
                    return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    violations.Add(new FieldViolation(name, "invalid"));
                    return null;
            }
        }

        var parsed = new ListRunFactsQuery(
            runId, Text("capabilityId"), Text("capabilityVersion"), Text("schemaVersion"), Text("nodeKey"),
            Text("fromMs"), Text("toMs"), Text("pageSize"), Text("continuationToken"));
        if (violations.Count > 0)
        {
            failure = OperationFailure.Validation([.. violations]);
            return false;
        }

        query = parsed;
        return true;
    }

    private async Task<JsonObject> RunJsonAsync(AuthorizedToolCall call, string state, CancellationToken cancellationToken)
    {
        var entries = await supersessions.ListMatchRunsAsync(call.MatchId, cancellationToken);
        var current = Domain.Analysis.RecordingSetVersionOrder.SelectCurrent(entries, e => e.OrderKey);
        var own = entries.FirstOrDefault(e => e.RunId == call.AnalysisRunId);

        JsonNode? supersededBy = null;
        if (own?.SupersededByRunId is { } supersedingId)
        {
            var superseding = entries.FirstOrDefault(e => e.RunId == supersedingId);
            if (superseding is not null)
            {
                supersededBy = new JsonObject
                {
                    ["runId"] = superseding.RunId,
                    ["recordingSetVersionId"] = superseding.Version.VersionId,
                    ["recordingSetFinalizedAt"] = superseding.Version.FinalizedAt,
                };
            }
        }

        return new JsonObject
        {
            ["runId"] = call.AnalysisRunId,
            ["state"] = state,
            ["isCurrent"] = own is not null && own.SupersededByRunId is null && current?.RunId == own.RunId,
            ["supersededBy"] = supersededBy,
        };
    }

    private static JsonObject MatchJson(Match match) => new()
    {
        ["matchId"] = match.Id,
        ["teamId"] = match.TeamId,
        ["opponent"] = new JsonObject { ["name"] = match.Opponent.Name.Value },
        ["kickoffAt"] = match.Details.KickoffAt,
        ["homeAway"] = match.Details.HomeAway.ToWire(),
        ["competition"] = match.Details.Competition,
    };

    private static JsonObject ProvenanceJson(FactNodeRecord node, FactRunRecord run) => new()
    {
        ["workflow"] = new JsonObject { ["id"] = run.WorkflowId, ["version"] = run.WorkflowVersion },
        ["analystProfile"] = new JsonObject { ["id"] = node.AnalystProfileId, ["digest"] = node.AnalystProfileDigest },
        ["imageDigest"] = node.ImageDigest,
        ["modelDigest"] = node.ModelDigest,
    };

    private static JsonObject CapabilityJson(FactNodeRecord node) => new()
    {
        ["id"] = node.CapabilityId,
        ["version"] = node.CapabilityVersion,
    };

    private static JsonObject StreamEntryJson(FactStreamEntry entry, FactRunRecord run) => new()
    {
        ["nodeKey"] = entry.Node.NodeKey,
        ["capability"] = CapabilityJson(entry.Node),
        ["schemaVersion"] = entry.Node.SchemaVersion,
        ["outputType"] = entry.Node.OutputType,
        ["executionScope"] = entry.Node.ExecutionScope,
        ["required"] = entry.Node.Required,
        ["factCount"] = entry.FactCount,
        ["provenance"] = ProvenanceJson(entry.Node, run),
        ["coverage"] = new JsonObject
        {
            ["accepted"] = entry.Coverage.Accepted,
            ["missing"] = entry.Coverage.Missing,
            ["failed"] = entry.Coverage.Failed,
            ["blocked"] = entry.Coverage.Blocked,
            ["skipped"] = entry.Coverage.Skipped,
        },
    };

    private static JsonObject FactsJson(RunFactsPage page)
    {
        var node = page.Stream;
        return new JsonObject
        {
            ["stream"] = new JsonObject
            {
                ["nodeKey"] = node.NodeKey,
                ["capability"] = CapabilityJson(node),
                ["schemaVersion"] = node.SchemaVersion,
                ["outputType"] = node.OutputType,
                ["executionScope"] = node.ExecutionScope,
                ["provenance"] = ProvenanceJson(node, page.Run),
            },
            ["range"] = new JsonObject { ["fromMs"] = page.FromMs, ["toMs"] = page.ToMs },
            ["items"] = new JsonArray([.. page.Items.Select(i => (JsonNode)new JsonObject
            {
                ["acceptedResultId"] = i.AcceptedResultId,
                ["ordinal"] = i.Ordinal,
                ["kind"] = i.Kind,
                ["startMs"] = i.StartMs,
                ["endMs"] = i.EndMs,
                ["confidence"] = i.Confidence,
                ["payload"] = JsonNode.Parse(i.PayloadJson),
            })]),
            ["coverage"] = new JsonArray([.. page.Coverage.Select(c => (JsonNode)new JsonObject
            {
                ["startMs"] = c.StartMs,
                ["endMs"] = c.EndMs,
                ["state"] = c.State.ToString().ToLowerInvariant(),
            })]),
            ["continuationToken"] = page.ContinuationToken,
        };
    }
}

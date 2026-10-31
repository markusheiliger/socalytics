using System.Globalization;
using System.Text.Json.Nodes;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record UnavailableAnalysisToolCommand(
    Guid AttemptId,
    string WorkloadClientId,
    string FencingToken,
    string ToolId,
    string CallId,
    JsonNode? Arguments);

/// <summary>
/// <c>get_player_stats</c> and the eight concept tools: they validate the optional <c>{ fromMs?, toMs? }</c> range, read nothing,
/// and answer <c>unavailable</c> with <c>no-accepted-producer</c> and the producing capability of the allowlist,
/// so an agent can never be given derived values for an analysis no accepted capability produces.
/// </summary>
public sealed class UnavailableAnalysisToolHandler(
    AuthorizeToolCallHandler authorize,
    IUnitOfWork unitOfWork,
    IAgentInvocationStore invocations,
    TimeProvider time)
{
    public async Task<OperationResult<ToolCallAnswer>> HandleAsync(UnavailableAnalysisToolCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!AgentToolAllowlist.TryGet(command.ToolId, out var entry) || entry.Answers != AgentToolAnswerKind.AnalysisUnavailable)
        {
            throw new ArgumentException("The tool does not answer as unavailable.", nameof(command));
        }

        var authorized = await authorize.HandleAsync(
            new AuthorizeToolCallCommand(
                command.AttemptId, command.WorkloadClientId, command.FencingToken, command.ToolId, command.CallId,
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

        var failure = ValidateRange(command.Arguments);
        ToolCallOutcome outcome;
        ToolCallReason? reason = null;
        JsonNode? result = null;
        string? producingCapability = null;
        if (failure is not null)
        {
            outcome = ToolCallOutcome.Invalid;
        }
        else
        {
            outcome = ToolCallOutcome.Unavailable;
            reason = ToolCallReason.NoAcceptedProducer;
            producingCapability = entry.ProducingCapability;
            result = new JsonObject
            {
                ["reason"] = ToolCallNames.ToWireName(ToolCallReason.NoAcceptedProducer),
                ["producingCapability"] = producingCapability,
                ["detail"] = producingCapability is null
                    ? "No catalog capability produces this analysis, so no accepted results exist for it."
                    : $"No accepted results exist for this analysis; the capability '{producingCapability}' that produces it has no accepted producer.",
            };
        }

        var lineage = new ToolCallLineageEntry(
            call.InvocationId, call.AttemptId, call.CallId, call.ToolId, outcome, reason, null, call.ArgumentsDigest,
            result is null ? null : ToolCallLineage.ResultDigest(result), [], [], producingCapability, time.GetUtcNow());
        await ToolCallLineage.AppendAsync(invocations, lineage, cancellationToken);
        AgentToolMetrics.CallRecorded(call.ToolId, ToolCallNames.ToWireName(outcome));
        await scope.CommitAsync(cancellationToken);

        return failure is not null ? failure : new ToolCallAnswer(outcome, reason, result);
    }

    // The range follows the fact read: non-negative whole milliseconds, and toMs after fromMs.
    private static OperationFailure? ValidateRange(JsonNode? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        if (arguments is not JsonObject root)
        {
            return OperationFailure.Validation(new FieldViolation("arguments", "invalid"));
        }

        var violations = root
            .Where(p => p.Key is not ("fromMs" or "toMs"))
            .Select(p => new FieldViolation(p.Key, "unknown"))
            .ToList();

        var fromMs = Parse(root, "fromMs", 0, violations);
        Parse(root, "toMs", fromMs is { } from ? from + 1 : 1, violations);

        return violations.Count > 0 ? OperationFailure.Validation([.. violations]) : null;
    }

    private static long? Parse(JsonObject root, string field, long minimum, List<FieldViolation> violations)
    {
        string? text;
        switch (root[field])
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var s):
                text = s;
                break;
            case JsonValue value when value.TryGetValue<long>(out var n):
                text = n.ToString(CultureInfo.InvariantCulture);
                break;
            default:
                violations.Add(new FieldViolation(field, "invalid"));
                return null;
        }

        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
        {
            violations.Add(new FieldViolation(field, "out-of-range"));
            return null;
        }

        return parsed;
    }
}

using System.Text.Json.Nodes;
using SocAlytics.Platform.Domain.AgentOrchestration.Tools;

namespace SocAlytics.Platform.Application.AgentOrchestration.Tools;

public sealed record ToolCallLineageEntry(
    Guid InvocationId,
    Guid AttemptId,
    string CallId,
    string ToolId,
    ToolCallOutcome Outcome,
    ToolCallReason? Reason,
    string? ReasonCode,
    string ArgumentsDigest,
    string? ResultDigest,
    IReadOnlyList<ToolCallFactEvidence> Facts,
    IReadOnlyList<ToolCallMediaEvidence> Media,
    string? ProducingCapability,
    DateTimeOffset RecordedAt);

/// <summary>The tool-call lineage entry of research R9: digests and bounded references, never results or links.</summary>
public static class ToolCallLineage
{
    public const string Kind = "tool-call";

    public static string ArgumentsDigest(JsonNode? arguments) =>
        Recordings.CanonicalJson.Digest(arguments?.DeepClone()).ToString();

    // Links and their expiry are removed first, so the digest neither depends on nor reveals them.
    public static string ResultDigest(JsonNode? result)
    {
        var copy = result?.DeepClone();
        StripMediaLinks(copy);
        return Recordings.CanonicalJson.Digest(copy).ToString();
    }

    public static string BuildPayload(ToolCallLineageEntry entry)
    {
        var facts = new JsonArray();
        foreach (var fact in entry.Facts)
        {
            facts.Add(new JsonObject { ["acceptedResultId"] = fact.AcceptedResultId.ToString(), ["ordinal"] = fact.Ordinal });
        }

        var media = new JsonArray();
        foreach (var part in entry.Media)
        {
            media.Add(new JsonObject { ["mediaDigest"] = part.MediaDigest, ["startMs"] = part.StartMs, ["endMs"] = part.EndMs });
        }

        var payload = new JsonObject
        {
            ["callId"] = entry.CallId,
            ["toolId"] = entry.ToolId,
            ["outcome"] = ToolCallNames.ToWireName(entry.Outcome),
            ["argumentsDigest"] = entry.ArgumentsDigest,
            ["evidence"] = new JsonObject { ["facts"] = facts, ["media"] = media },
        };

        var reason = entry.ReasonCode ?? (entry.Reason is { } r ? ToolCallNames.ToWireName(r) : null);
        if (reason is not null)
        {
            payload["reason"] = reason;
        }

        if (entry.ResultDigest is not null)
        {
            payload["resultDigest"] = entry.ResultDigest;
        }

        if (entry.ProducingCapability is not null)
        {
            payload["producingCapability"] = entry.ProducingCapability;
        }

        return payload.ToJsonString();
    }

    // Callers hold the invocation lock, so the check and the append cannot interleave; the first entry for a call stands.
    // Returns the assigned sequence, or null when the call already has an entry.
    public static async Task<int?> AppendAsync(
        IAgentInvocationStore invocations, ToolCallLineageEntry entry, CancellationToken cancellationToken)
    {
        var lineage = await invocations.ReadLineageAsync(entry.InvocationId, cancellationToken);
        if (lineage.Entries.Any(e => IsEntryFor(e, entry.AttemptId, entry.CallId)))
        {
            return null;
        }

        return await invocations.AppendLineageAsync(
            new NewAgentLineageEntry(entry.InvocationId, Kind, entry.AttemptId, BuildPayload(entry), entry.RecordedAt),
            cancellationToken);
    }

    private static bool IsEntryFor(AgentLineageEntryRecord record, Guid attemptId, string callId)
    {
        if (record.Kind != Kind || record.AttemptId != attemptId)
        {
            return false;
        }

        return JsonNode.Parse(record.PayloadJson) is JsonObject payload
            && payload["callId"]?.GetValue<string>() == callId;
    }

    private static void StripMediaLinks(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["media"] is JsonObject media)
                {
                    media.Remove("url");
                    media.Remove("expiresAt");
                }

                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    StripMediaLinks(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    StripMediaLinks(child);
                }

                break;
        }
    }
}

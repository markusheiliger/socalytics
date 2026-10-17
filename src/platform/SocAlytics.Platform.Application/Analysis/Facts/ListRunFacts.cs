using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Facts;

public sealed record ListRunFactsQuery(
    Guid RunId,
    string? CapabilityId,
    string? CapabilityVersion,
    string? SchemaVersion,
    string? NodeKey,
    string? FromMs,
    string? ToMs,
    string? PageSize,
    string? ContinuationToken);

public sealed record RunFactsPage(
    FactRunRecord Run,
    FactNodeRecord Stream,
    long? FromMs,
    long? ToMs,
    IReadOnlyList<FactItemRecord> Items,
    IReadOnlyList<FactCoverageWindow> Coverage,
    string? ContinuationToken,
    string ETagBasis);

public sealed partial class ListRunFactsHandler(
    IUnitOfWork unitOfWork, IFactQueries queries, IAccessAuthorizer authorizer, IOptions<FactReadOptions> options)
{
    public const string CapabilityNotInRun = "analysis-capability-not-in-run";
    public const string CapabilityAmbiguous = "analysis-capability-ambiguous";

    private sealed record TokenPayload(long S, long E, Guid R, int O, string H);

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,99}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex VersionPattern();

    public async Task<OperationResult<RunFactsPage>> HandleAsync(ListRunFactsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        RequireMatch(violations, "capabilityId", query.CapabilityId, KeyPattern());
        RequireMatch(violations, "capabilityVersion", query.CapabilityVersion, VersionPattern());
        RequireMatch(violations, "schemaVersion", query.SchemaVersion, VersionPattern());
        if (query.NodeKey is not null && !KeyPattern().IsMatch(query.NodeKey))
        {
            violations.Add(new FieldViolation("nodeKey", "invalid"));
        }

        var fromMs = ParseMs(violations, "fromMs", query.FromMs, 0);
        var toMs = ParseMs(violations, "toMs", query.ToMs, fromMs is { } f ? f + 1 : 1);

        var pageSize = options.Value.DefaultPageSize;
        if (query.PageSize is not null
            && (!int.TryParse(query.PageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize)
                || pageSize < 1 || pageSize > options.Value.MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        TokenPayload? token = null;
        if (query.ContinuationToken is not null && (query.ContinuationToken.Length > 512 || !TryDecode(query.ContinuationToken, out token)))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("analysis-run", query.RunId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var run = await queries.GetRunAsync(query.RunId, cancellationToken);
        if (run is null)
        {
            return OperationFailure.NotFound();
        }

        var nodes = await queries.ResolveNodesAsync(
            query.RunId, query.CapabilityId!, query.CapabilityVersion!, query.SchemaVersion!, query.NodeKey, cancellationToken);
        if (nodes.Count == 0)
        {
            return new OperationFailure(OperationFailureKind.NotFound, CapabilityNotInRun, [], null);
        }

        if (nodes.Count > 1)
        {
            return OperationFailure.Validation(CapabilityAmbiguous);
        }

        var node = nodes[0];
        var selectionHash = Hash($"{run.RunId:N}|{node.NodeId:N}|{node.SchemaVersion}|{fromMs}|{toMs}");
        FactPageKey? after = null;
        if (token is not null)
        {
            if (token.H != selectionHash)
            {
                return OperationFailure.Validation(new FieldViolation("continuationToken", "invalid"));
            }

            after = new FactPageKey(token.S, token.E, token.R, token.O);
        }

        var page = await queries.QueryPageAsync(new FactPageQuery(run.RunId, node.NodeId, fromMs, toMs, after, pageSize), cancellationToken);
        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        var next = page.HasMore && last is not null ? Encode(last, selectionHash) : null;

        var inputs = await queries.GetCoverageInputsAsync(run.RunId, node.NodeId, cancellationToken);
        var coverage = FactCoverageBuilder.Build(
            new FactCoverageInput(
                node.Required,
                [.. inputs.Jobs.Select(j => new FactCoverageJob(
                    j.SegmentNumber, Enum.Parse<LogicalJobState>(j.State, ignoreCase: true), j.HasAcceptedResult))],
                inputs.SegmentDurationMs,
                inputs.RecordingCoverageEndMs ?? 0),
            fromMs,
            toMs);

        var etagBasis = $"{run.VersionBasis}|{selectionHash}|{query.ContinuationToken}|{pageSize}";
        return new RunFactsPage(run, node, fromMs, toMs, page.Items, coverage, next, etagBasis);
    }

    private static void RequireMatch(List<FieldViolation> violations, string field, string? value, Regex pattern)
    {
        if (string.IsNullOrEmpty(value))
        {
            violations.Add(new FieldViolation(field, "required"));
        }
        else if (!pattern.IsMatch(value))
        {
            violations.Add(new FieldViolation(field, "invalid"));
        }
    }

    private static long? ParseMs(List<FieldViolation> violations, string field, string? value, long minimum)
    {
        if (value is null)
        {
            return null;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < minimum)
        {
            violations.Add(new FieldViolation(field, "out-of-range"));
            return null;
        }

        return parsed;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32];

    private static string Encode(FactItemRecord last, string selectionHash)
    {
        var json = JsonSerializer.Serialize(new { s = last.StartMs, e = last.EndMs, r = last.AcceptedResultId, o = last.Ordinal, h = selectionHash });
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
    }

    private static bool TryDecode(string text, out TokenPayload? token)
    {
        token = null;
        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(text));
            var root = document.RootElement;
            token = new TokenPayload(
                root.GetProperty("s").GetInt64(), root.GetProperty("e").GetInt64(), root.GetProperty("r").GetGuid(),
                root.GetProperty("o").GetInt32(), root.GetProperty("h").GetString() ?? string.Empty);
            return token.S >= 0 && token.E > token.S && token.O >= 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}

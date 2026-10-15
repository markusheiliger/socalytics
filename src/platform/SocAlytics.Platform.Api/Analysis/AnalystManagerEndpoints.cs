using System.Text.Json;
using System.Text.RegularExpressions;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Api.Recordings;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Results;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record ClaimRequestDto(Guid ReadinessOccurrenceId, Guid MessageId);

internal sealed record ClaimGrantedDto(
	Guid AttemptId, Guid LogicalJobId, Guid ReadinessOccurrenceId, int AttemptNumber, long FencingToken,
	DateTimeOffset LeaseExpiresAt, int LeaseSeconds, int StaleTimeoutSeconds, int ExecutionTimeoutSeconds, bool Replayed);

internal sealed record HeartbeatRequestDto(Guid LogicalJobId, long FencingToken, DateTimeOffset ObservedAt, string ExecutionState, int? ProgressPercent);

internal sealed record CompletionOutcomeDto(Guid AttemptId, Guid LogicalJobId, string Outcome, Guid? AcceptedResultId, bool Replayed, string LogicalJobState, string? Reason = null);

internal sealed record ResultObjectDto(string? Name, long SizeBytes, string? Digest);

internal sealed record ResultGrantsRequestDto(long FencingToken, List<ResultObjectDto?>? Objects);

internal sealed record ResultGrantDto(string Name, string Method, string Url, IReadOnlyDictionary<string, string> RequiredHeaders, string ExpiresAt);

internal sealed record ResultGrantsDto(string ResultLocation, IReadOnlyList<ResultGrantDto> Grants);

internal sealed record UpstreamCapabilityDto(string Id, string Version);

internal sealed record UpstreamDependencyDto(UpstreamCapabilityDto Capability, string SchemaVersion, string EdgeKind);

internal sealed record UpstreamSegmentScopeDto(string SegmentId, long WindowStartMs, long WindowEndMs);

internal sealed record UpstreamScopeDto(
	[property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] UpstreamSegmentScopeDto? Segment,
	[property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? Match);

internal sealed record UpstreamGrantDto(string Name, string Url, string ExpiresAt);

internal sealed record UpstreamResultDto(
	string AcceptedResultId, UpstreamScopeDto Scope, string ManifestChecksum, JsonElement Manifest, IReadOnlyList<UpstreamGrantDto> Grants);

internal sealed record UpstreamCoverageDto(
	[property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? SegmentId,
	[property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? Match,
	string State);

internal sealed record UpstreamResultsDto(
	string ContractVersion, UpstreamDependencyDto Upstream, IReadOnlyList<UpstreamResultDto> Results,
	IReadOnlyList<UpstreamCoverageDto> Coverage, string? ContinuationToken);

internal sealed record LeaseRenewedDto(Guid AttemptId, DateTimeOffset LeaseExpiresAt);

internal static partial class AnalystManagerEndpoints
{
	private const string ClaimObsolete = "claim-obsolete";
	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

	[GeneratedRegex("^[A-Za-z0-9._\\-]{1,128}$")]
	private static partial Regex KeyPattern();

	public static IEndpointRouteBuilder MapAnalystManagerEndpoints(this IEndpointRouteBuilder routes)
	{
		routes.MapPost("/api/v1/analysis/logical-jobs/{logicalJobId:guid}/claims", ClaimAsync)
			.RequireAnalystManager()
			.WithName("claimLogicalJob")
			.WithTags("Analyst Manager attempts")
			.Produces<ClaimGrantedDto>()
			.Produces<ClaimGrantedDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/analysis/attempts/{attemptId:guid}/heartbeats", HeartbeatAsync)
			.RequireAnalystManager()
			.WithName("renewAttemptLease")
			.WithTags("Analyst Manager attempts")
			.Produces<LeaseRenewedDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		routes.MapPost("/api/v1/analysis/attempts/{attemptId:guid}/completion", CompleteAsync)
			.RequireAnalystManager()
			.AddEndpointFilter<ObjectStorageUnavailableFilter>()
			.WithName("completeAttempt")
			.WithTags("Analyst Manager attempts")
			.Produces<CompletionOutcomeDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		routes.MapPost("/api/v1/analysis/attempts/{attemptId:guid}/result-grants", IssueResultGrantsAsync)
			.RequireAnalystManager()
			.AddEndpointFilter<ObjectStorageUnavailableFilter>()
			.WithName("issueAnalysisResultGrants")
			.WithTags("Analyst Manager attempts")
			.Produces<ResultGrantsDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		routes.MapGet("/api/v1/analysis/attempts/{attemptId:guid}/upstream-results", ListUpstreamResultsAsync)
			.RequireAnalystManager()
			.AddEndpointFilter<ObjectStorageUnavailableFilter>()
			.WithName("listAnalysisUpstreamResults")
			.WithTags("Analyst Manager attempts")
			.Produces<UpstreamResultsDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		return routes;
	}

	private static string Timestamp(DateTimeOffset value) =>
		value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture);

	private static async Task<IResult> ListUpstreamResultsAsync(
		Guid attemptId, HttpContext http, ListUpstreamResultsHandler handler, ILoggerFactory loggers, CancellationToken cancellationToken)
	{
		var q = http.Request.Query;
		var violations = new List<FieldViolation>();
		long? Long(string name)
		{
			if (!q.TryGetValue(name, out var v) || v.Count == 0)
			{
				return null;
			}

			if (v.Count == 1 && long.TryParse(v[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n))
			{
				return n;
			}

			violations.Add(new FieldViolation(name, "type"));
			return null;
		}

		string? Text(string name) => q.TryGetValue(name, out var v) && v.Count == 1 ? v[0] : null;

		var fencingToken = Long("fencingToken");
		var matchStart = Long("matchStartMs");
		var matchEnd = Long("matchEndMs");
		var pageSize = Long("pageSize");
		if (pageSize is > int.MaxValue)
		{
			violations.Add(new FieldViolation("pageSize", "maximum"));
		}

		if (violations.Count > 0)
		{
			return ProblemResults.From(OperationFailure.Validation(violations.ToArray()));
		}

		var segmentIds = q.TryGetValue("segmentIds", out var ids)
			? ids.SelectMany(i => (i ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList()
			: null;
		var result = await handler.HandleAsync(
			new ListUpstreamResultsQuery(
				attemptId, fencingToken, Text("capabilityId"), Text("capabilityVersion"), Text("schemaVersion"),
				segmentIds, matchStart, matchEnd, (int?)pageSize, Text("continuationToken")),
			cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		loggers.CreateLogger("SocAlytics.Platform.Api.Analysis.UpstreamResults").LogInformation(
			"Read {ResultCount} upstream results for attempt {AttemptId} of job {LogicalJobId} in run {RunId}",
			v.Results.Count, attemptId, v.LogicalJobId, v.RunId);
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(
			new UpstreamResultsDto(
				"1.0.0",
				new UpstreamDependencyDto(new UpstreamCapabilityDto(v.CapabilityId, v.CapabilityVersion), v.SchemaVersion, v.EdgeKind),
				v.Results.Select(r => new UpstreamResultDto(
					r.AcceptedResultId.ToString(),
					r.SegmentId is null
						? new UpstreamScopeDto(null, true)
						: new UpstreamScopeDto(new UpstreamSegmentScopeDto(r.SegmentId, r.WindowStartMs!.Value, r.WindowEndMs!.Value), null),
					r.ManifestChecksum, r.Manifest,
					r.Grants.Select(g => new UpstreamGrantDto(g.Name, g.Url.AbsoluteUri, Timestamp(g.ExpiresAt))).ToList())).ToList(),
				v.Coverage.Select(c => new UpstreamCoverageDto(c.SegmentId, c.SegmentId is null ? true : null, c.State.ToString().ToLowerInvariant())).ToList(),
				v.ContinuationToken));
	}

	private static async Task<IResult> IssueResultGrantsAsync(
		Guid attemptId, HttpContext http, IssueResultGrantsHandler handler, ILoggerFactory loggers, CancellationToken cancellationToken)
	{
		ResultGrantsRequestDto? body;
		try
		{
			body = await JsonSerializer.DeserializeAsync<ResultGrantsRequestDto>(http.Request.Body, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null || body.FencingToken <= 0)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var command = new IssueResultGrantsCommand(
			attemptId, body.FencingToken, body.Objects?.Select(o => new ResultObjectRequest(o?.Name, o?.SizeBytes ?? 0, o?.Digest)).ToList());
		var result = await handler.HandleAsync(command, cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		loggers.CreateLogger("SocAlytics.Platform.Api.Analysis.ResultGrants").LogInformation(
			"Issued {GrantCount} result grants for attempt {AttemptId} of job {LogicalJobId} in run {RunId}: {ObjectNames}",
			v.Grants.Count, attemptId, v.LogicalJobId, v.RunId, string.Join(", ", v.Grants.Select(g => g.Name)));
		http.Response.Headers.CacheControl = "no-store";
		return Results.Ok(new ResultGrantsDto(
			v.ResultLocation, v.Grants.Select(g => new ResultGrantDto(g.Name, g.Method, g.Url.AbsoluteUri, g.RequiredHeaders, g.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", System.Globalization.CultureInfo.InvariantCulture))).ToList()));
	}

	private static async Task<IResult> CompleteAsync(Guid attemptId, HttpContext http, CompleteAttemptHandler handler, CancellationToken cancellationToken)
	{
		JsonElement body;
		try
		{
			using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
			body = doc.RootElement.Clone();
		}
		catch (JsonException)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		if (body.ValueKind != JsonValueKind.Object)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("", "type")));
		}

		var result = await handler.HandleAsync(new CompleteAttemptCommand(attemptId, body), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var v = result.Value;
		return Results.Ok(new CompletionOutcomeDto(v.AttemptId, v.LogicalJobId, v.Outcome, v.AcceptedResultId, v.Replayed, v.LogicalJobState, v.Reason));
	}

	private static async Task<IResult> HeartbeatAsync(Guid attemptId, HttpContext http, RenewAttemptLeaseHandler handler, CancellationToken cancellationToken)
	{
		HeartbeatRequestDto? body;
		try
		{
			body = await JsonSerializer.DeserializeAsync<HeartbeatRequestDto>(http.Request.Body, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null || body.LogicalJobId == Guid.Empty || body.FencingToken <= 0
			|| body.ExecutionState is not ("preparing" or "running" or "finalizing")
			|| body.ProgressPercent is < 0 or > 100)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var result = await handler.HandleAsync(new RenewAttemptLeaseCommand(attemptId, body.LogicalJobId, body.FencingToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new LeaseRenewedDto(result.Value.AttemptId, result.Value.LeaseExpiresAt))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> ClaimAsync(Guid logicalJobId, HttpContext http, ClaimLogicalJobHandler handler, CancellationToken cancellationToken)
	{
		var keyHeader = http.Request.Headers["Idempotency-Key"];
		if (keyHeader.Count == 0)
		{
			return ProblemResults.Problem(SharedProblemCodes.StatusOf(SharedProblemCodes.IdempotencyKeyMissing), SharedProblemCodes.IdempotencyKeyMissing);
		}

		var key = keyHeader.Count == 1 ? keyHeader[0] : null;
		if (key is null || !KeyPattern().IsMatch(key))
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("Idempotency-Key", "invalid")));
		}

		ClaimRequestDto? body;
		try
		{
			body = await JsonSerializer.DeserializeAsync<ClaimRequestDto>(http.Request.Body, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null || body.ReadinessOccurrenceId == Guid.Empty || body.MessageId == Guid.Empty)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var result = await handler.HandleAsync(new ClaimLogicalJobCommand(logicalJobId, key, body.ReadinessOccurrenceId, body.MessageId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var g = result.Value;
		var dto = new ClaimGrantedDto(
			g.AttemptId, g.LogicalJobId, g.ReadinessOccurrenceId, g.AttemptNumber, g.FencingToken, g.LeaseExpiresAt,
			g.LeaseSeconds, g.StaleTimeoutSeconds, g.ExecutionTimeoutSeconds, g.Replayed);
		return g.Replayed
			? Results.Ok(dto)
			: Results.Created($"/api/v1/analysis/attempts/{g.AttemptId}", dto);
	}
}

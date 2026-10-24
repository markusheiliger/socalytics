using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Queries;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record AnalysisRunCancelledByDto(Guid MemberId, string AccountName);

internal sealed record AnalysisRunSupersededByDto(Guid RunId, Guid RecordingSetVersionId, DateTimeOffset RecordingSetFinalizedAt);

internal sealed record CancelAnalysisRunRequest(string? Reason);

internal sealed record AnalysisRunSummaryDto(
	Guid RunId, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion, string State,
	string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? RunningAt, DateTimeOffset? TerminatedAt, long Version,
	AnalysisRunCancelledByDto? CancelledBy, string? CancellationReason, string? CancellationCause, bool IsCurrent,
	AnalysisRunSupersededByDto? SupersededBy, DateTimeOffset? SupersededAt)
{
	public static AnalysisRunSummaryDto From(AnalysisRunSummaryView v) => new(
		v.RunId, v.TeamId, v.MatchId, v.RecordingSetVersionId, v.WorkflowId, v.WorkflowVersion, v.State, v.FailureReason,
		v.CreatedAt, v.RunningAt, v.TerminatedAt, v.Version, MapCancelledBy(v.CancelledBy), v.CancellationReason,
		v.CancellationCause, v.IsCurrent, MapSupersededBy(v.SupersededBy), v.SupersededAt);

	internal static AnalysisRunSupersededByDto? MapSupersededBy(AnalysisRunSupersededByView? v) =>
		v is null ? null : new(v.RunId, v.RecordingSetVersionId, v.RecordingSetFinalizedAt);

	internal static AnalysisRunCancelledByDto? MapCancelledBy(AnalysisRunCancelledByView? v) =>
		v is null ? null : new(v.MemberId, v.AccountName);
}

internal sealed record AnalysisJobCountsDto(int Waiting, int Ready, int Claimed, int Completed, int Failed, int Blocked, int Cancelled);

internal sealed record AnalysisNodeInputCoverageDto(
	string UpstreamNodeKey, string CoverageMode, decimal? MinimumAcceptedFraction, int Accepted, int Failed, int Blocked, int Skipped,
	int Missing);

internal sealed record AnalysisNodeStatusDto(
	Guid NodeId, string NodeKey, string CapabilityId, string CapabilityVersion, string Tier, string ExecutionScope, string State,
	string? BlockedByNodeKey, AnalysisJobCountsDto Jobs, IReadOnlyList<AnalysisNodeInputCoverageDto>? InputCoverage);

internal sealed record AnalysisRunStatusDto(
	Guid RunId, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion, string State,
	string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? RunningAt, DateTimeOffset? TerminatedAt, long Version,
	AnalysisRunCancelledByDto? CancelledBy, string? CancellationReason, string? CancellationCause, bool IsCurrent,
	AnalysisRunSupersededByDto? SupersededBy, DateTimeOffset? SupersededAt,
	IReadOnlyList<string> RequestedCapabilities, IReadOnlyList<AnalysisNodeStatusDto> Nodes)
{
	public static AnalysisRunStatusDto From(AnalysisRunStatusView s)
	{
		var r = s.Run;
		return new(
			r.RunId, r.TeamId, r.MatchId, r.RecordingSetVersionId, r.WorkflowId, r.WorkflowVersion, r.State, r.FailureReason,
			r.CreatedAt, r.RunningAt, r.TerminatedAt, r.Version,
			AnalysisRunSummaryDto.MapCancelledBy(r.CancelledBy), r.CancellationReason,
			r.CancellationCause, r.IsCurrent, AnalysisRunSummaryDto.MapSupersededBy(r.SupersededBy), r.SupersededAt, s.RequestedCapabilities,
			[.. s.Nodes.Select(n => new AnalysisNodeStatusDto(
				n.NodeId, n.NodeKey, n.CapabilityId, n.CapabilityVersion, n.Tier, n.ExecutionScope, n.State, n.BlockedByNodeKey,
				new AnalysisJobCountsDto(n.Jobs.Waiting, n.Jobs.Ready, n.Jobs.Claimed, n.Jobs.Completed, n.Jobs.Failed, n.Jobs.Blocked, n.Jobs.Cancelled),
				n.InputCoverage is null
					? null
					: [.. n.InputCoverage.Select(c => new AnalysisNodeInputCoverageDto(
						c.UpstreamNodeKey, c.CoverageMode, c.MinimumAcceptedFraction, c.Accepted, c.Failed, c.Blocked, c.Skipped,
						c.Missing))]))]);
	}
}

internal sealed record AnalysisRunSummaryPageDto(IReadOnlyList<AnalysisRunSummaryDto> Items, string? ContinuationToken);

internal sealed record AnalysisRunRequestAcceptedDto(
	Guid RunId, bool Created, string State, Guid? CurrentRunId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion,
	string StatusLink)
{
	public static AnalysisRunRequestAcceptedDto From(RequestAnalysisRunResult r) => new(
		r.RunId, r.Created, r.State.ToString(), r.CurrentRunId, r.RecordingSetVersionId, r.WorkflowId, r.WorkflowVersion,
		AnalysisRunEndpoints.StatusLinkOf(r.RunId));
}

internal static class AnalysisRunEndpoints
{
	internal static string StatusLinkOf(Guid runId) => $"/api/v1/analysis/runs/{runId}";

	private static TBuilder WithIdempotencyKeyParameter<TBuilder>(this TBuilder builder)
		where TBuilder : IEndpointConventionBuilder =>
		builder.AddOpenApiOperationTransformer((operation, _, _) =>
		{
			operation.Parameters ??= [];
			operation.Parameters.Add(new Microsoft.OpenApi.OpenApiParameter
			{
				Name = "Idempotency-Key",
				In = Microsoft.OpenApi.ParameterLocation.Header,
				Required = true,
				Schema = new Microsoft.OpenApi.OpenApiSchema { Type = Microsoft.OpenApi.JsonSchemaType.String, MinLength = 1, MaxLength = 255 },
			});
			return Task.CompletedTask;
		});

	public static IEndpointRouteBuilder MapAnalysisRunEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapPost("/matches/{matchId:guid}/analysis-runs", RequestRunAsync)
			.WithName("requestAnalysisRun")
			.WithTags("Analysis runs")
			.WithIdempotencyKeyParameter()
			.Produces<AnalysisRunRequestAcceptedDto>(StatusCodes.Status202Accepted)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		members.MapGet("/analysis/runs/{runId:guid}", GetRunAsync)
			.WithName("getAnalysisRun")
			.WithTags("Analysis runs")
			.Produces<AnalysisRunStatusDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/matches/{matchId:guid}/analysis-runs", ListAsync)
			.WithName("listMatchAnalysisRuns")
			.WithTags("Analysis runs")
			.Produces<AnalysisRunSummaryPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/analysis/runs/{runId:guid}/cancellation", CancelRunAsync)
			.Accepts<CancelAnalysisRunRequest>(false, "application/json")
			.WithName("cancelAnalysisRun")
			.WithTags("Analysis runs")
			.Produces<AnalysisRunStatusDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		return routes;
	}

	private static async Task<IResult> RequestRunAsync(Guid matchId, HttpContext http, RequestAnalysisRunHandler handler, CancellationToken cancellationToken)
	{
		var keyHeader = http.Request.Headers["Idempotency-Key"];
		var result = await handler.HandleAsync(
			new RequestAnalysisRunCommand(matchId, keyHeader.Count == 1 ? keyHeader[0] : null), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var location = StatusLinkOf(result.Value.RunId);
		http.Response.Headers.Location = location;
		http.Response.Headers.CacheControl = "no-store";
		return Results.Json(AnalysisRunRequestAcceptedDto.From(result.Value), statusCode: StatusCodes.Status202Accepted);
	}

	private static async Task<IResult> CancelRunAsync(Guid runId, HttpContext http, CancelAnalysisRunHandler handler, CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		string? reason = null;
		if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
		{
			if (!http.Request.HasJsonContentType())
			{
				return ProblemResults.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupported-media-type");
			}

			try
			{
				reason = (await http.Request.ReadFromJsonAsync<CancelAnalysisRunRequest>(cancellationToken))?.Reason;
			}
			catch (System.Text.Json.JsonException)
			{
				return ProblemResults.From(OperationFailure.Validation(new FieldViolation("reason", "invalid")));
			}
		}

		var result = await handler.HandleAsync(new CancelAnalysisRunCommand(runId, version.Value, reason), cancellationToken);
		return result.IsSuccess ? Results.Ok(AnalysisRunStatusDto.From(result.Value)) : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> GetRunAsync(Guid runId, HttpContext http, GetAnalysisRunHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetAnalysisRunQuery(runId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{runId}:{result.Value.VersionBasis}")))[..32];
		var etag = $"W/\"{hash}\"";
		http.Response.Headers.ETag = etag;
		var candidates = http.Request.Headers.IfNoneMatch.ToString()
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		return candidates.Any(c => c == "*" || c == etag || c == etag[2..])
			? Results.StatusCode(StatusCodes.Status304NotModified)
			: Results.Ok(AnalysisRunStatusDto.From(result.Value));
	}

	private static async Task<IResult> ListAsync(Guid matchId, string? pageSize, string? continuationToken, ListMatchAnalysisRunsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListMatchAnalysisRunsQuery(matchId, pageSize, continuationToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new AnalysisRunSummaryPageDto([.. result.Value.Items.Select(AnalysisRunSummaryDto.From)], result.Value.ContinuationToken))
			: ProblemResults.From(result.Failure);
	}
}

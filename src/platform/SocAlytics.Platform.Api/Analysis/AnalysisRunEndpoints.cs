using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Analysis.Queries;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record AnalysisRunSummaryDto(
	Guid RunId, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion, string State,
	string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? RunningAt, DateTimeOffset? TerminatedAt, long Version)
{
	public static AnalysisRunSummaryDto From(AnalysisRunSummaryView v) => new(
		v.RunId, v.TeamId, v.MatchId, v.RecordingSetVersionId, v.WorkflowId, v.WorkflowVersion, v.State, v.FailureReason,
		v.CreatedAt, v.RunningAt, v.TerminatedAt, v.Version);
}

internal sealed record AnalysisJobCountsDto(int Waiting, int Ready, int Claimed, int Completed, int Failed, int Blocked);

internal sealed record AnalysisNodeInputCoverageDto(
	string UpstreamNodeKey, string CoverageMode, decimal? MinimumAcceptedFraction, int Accepted, int Failed, int Blocked, int Skipped,
	int Missing);

internal sealed record AnalysisNodeStatusDto(
	Guid NodeId, string NodeKey, string CapabilityId, string CapabilityVersion, string Tier, string ExecutionScope, string State,
	string? BlockedByNodeKey, AnalysisJobCountsDto Jobs, IReadOnlyList<AnalysisNodeInputCoverageDto>? InputCoverage);

internal sealed record AnalysisRunStatusDto(
	Guid RunId, Guid TeamId, Guid MatchId, Guid RecordingSetVersionId, string WorkflowId, string WorkflowVersion, string State,
	string? FailureReason, DateTimeOffset CreatedAt, DateTimeOffset? RunningAt, DateTimeOffset? TerminatedAt, long Version,
	IReadOnlyList<string> RequestedCapabilities, IReadOnlyList<AnalysisNodeStatusDto> Nodes)
{
	public static AnalysisRunStatusDto From(AnalysisRunStatusView s)
	{
		var r = s.Run;
		return new(
			r.RunId, r.TeamId, r.MatchId, r.RecordingSetVersionId, r.WorkflowId, r.WorkflowVersion, r.State, r.FailureReason,
			r.CreatedAt, r.RunningAt, r.TerminatedAt, r.Version, s.RequestedCapabilities,
			[.. s.Nodes.Select(n => new AnalysisNodeStatusDto(
				n.NodeId, n.NodeKey, n.CapabilityId, n.CapabilityVersion, n.Tier, n.ExecutionScope, n.State, n.BlockedByNodeKey,
				new AnalysisJobCountsDto(n.Jobs.Waiting, n.Jobs.Ready, n.Jobs.Claimed, n.Jobs.Completed, n.Jobs.Failed, n.Jobs.Blocked),
				n.InputCoverage is null
					? null
					: [.. n.InputCoverage.Select(c => new AnalysisNodeInputCoverageDto(
						c.UpstreamNodeKey, c.CoverageMode, c.MinimumAcceptedFraction, c.Accepted, c.Failed, c.Blocked, c.Skipped,
						c.Missing))]))]);
	}
}

internal sealed record AnalysisRunSummaryPageDto(IReadOnlyList<AnalysisRunSummaryDto> Items, string? ContinuationToken);

internal static class AnalysisRunEndpoints
{
	public static IEndpointRouteBuilder MapAnalysisRunEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
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
		return routes;
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

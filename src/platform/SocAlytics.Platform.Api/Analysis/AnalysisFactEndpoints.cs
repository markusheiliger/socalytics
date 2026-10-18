using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Analysis.Facts;
using SocAlytics.Platform.Application.Analysis.Results;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record FactRunDto(Guid RunId, Guid TeamId, Guid MatchId, string State);

internal sealed record FactCapabilityDto(string Id, string Version);

internal sealed record FactProfileDto(string Id, string Digest);

internal sealed record FactWorkflowDto(string Id, string Version);

internal sealed record FactProvenanceDto(FactWorkflowDto Workflow, FactProfileDto AnalystProfile, string ImageDigest, string? ModelDigest);

internal sealed record FactStreamDto(
	string NodeKey, FactCapabilityDto Capability, string SchemaVersion, string OutputType, string ExecutionScope, FactProvenanceDto Provenance);

internal sealed record FactStreamCoverageDto(int Accepted, int Missing, int Failed, int Blocked, int Skipped);

internal sealed record FactStreamEntryDto(
	string NodeKey, FactCapabilityDto Capability, string SchemaVersion, string OutputType, string ExecutionScope, bool Required,
	long FactCount, FactProvenanceDto Provenance, FactStreamCoverageDto Coverage);

internal sealed record FactStreamListDto(FactRunDto Run, IReadOnlyList<FactStreamEntryDto> Items)
{
	public static FactStreamListDto From(RunFactStreams streams) => new(
		new FactRunDto(streams.Run.RunId, streams.Run.TeamId, streams.Run.MatchId, streams.Run.State),
		[.. streams.Streams.Select(e => new FactStreamEntryDto(
			e.Node.NodeKey, new FactCapabilityDto(e.Node.CapabilityId, e.Node.CapabilityVersion), e.Node.SchemaVersion, e.Node.OutputType,
			e.Node.ExecutionScope, e.Node.Required, e.FactCount,
			new FactProvenanceDto(
				new FactWorkflowDto(streams.Run.WorkflowId, streams.Run.WorkflowVersion),
				new FactProfileDto(e.Node.AnalystProfileId, e.Node.AnalystProfileDigest), e.Node.ImageDigest, e.Node.ModelDigest),
			new FactStreamCoverageDto(e.Coverage.Accepted, e.Coverage.Missing, e.Coverage.Failed, e.Coverage.Blocked, e.Coverage.Skipped)))]);
}

internal sealed record FactRangeDto(long? FromMs, long? ToMs);

internal sealed record FactItemDto(Guid AcceptedResultId, int Ordinal, string Kind, long StartMs, long EndMs, double Confidence, JsonElement Payload);

internal sealed record FactCoverageDto(long StartMs, long EndMs, string State);

internal sealed record FactPageDto(
	FactRunDto Run, FactStreamDto Stream, FactRangeDto Range, IReadOnlyList<FactItemDto> Items,
	IReadOnlyList<FactCoverageDto> Coverage, string? ContinuationToken)
{
	public static FactPageDto From(RunFactsPage page)
	{
		var n = page.Stream;
		return new(
			new FactRunDto(page.Run.RunId, page.Run.TeamId, page.Run.MatchId, page.Run.State),
			new FactStreamDto(
				n.NodeKey, new FactCapabilityDto(n.CapabilityId, n.CapabilityVersion), n.SchemaVersion, n.OutputType, n.ExecutionScope,
				new FactProvenanceDto(
					new FactWorkflowDto(page.Run.WorkflowId, page.Run.WorkflowVersion),
					new FactProfileDto(n.AnalystProfileId, n.AnalystProfileDigest), n.ImageDigest, n.ModelDigest)),
			new FactRangeDto(page.FromMs, page.ToMs),
			[.. page.Items.Select(i => new FactItemDto(
				i.AcceptedResultId, i.Ordinal, i.Kind, i.StartMs, i.EndMs, i.Confidence, JsonDocument.Parse(i.PayloadJson).RootElement.Clone()))],
			[.. page.Coverage.Select(c => new FactCoverageDto(c.StartMs, c.EndMs, c.State.ToString().ToLowerInvariant()))],
			page.ContinuationToken);
	}
}

internal static class AnalysisFactEndpoints
{
	public static IEndpointRouteBuilder MapAnalysisFactEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/analysis/runs/{runId:guid}/fact-streams", ListFactStreamsAsync)
			.WithName("listAnalysisRunFactStreams")
			.WithTags("Analysis facts")
			.Produces<FactStreamListDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/analysis/runs/{runId:guid}/facts", ListFactsAsync)
			.WithName("listAnalysisRunFacts")
			.WithTags("Analysis facts")
			.Produces<FactPageDto>()
			.Produces(StatusCodes.Status304NotModified)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status404NotFound);
		return routes;
	}

	private static async Task<IResult> ListFactsAsync(
		Guid runId, string? capabilityId, string? capabilityVersion, string? schemaVersion, string? nodeKey, string? fromMs, string? toMs,
		string? pageSize, string? continuationToken, HttpContext http, ListRunFactsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(
			new ListRunFactsQuery(runId, capabilityId, capabilityVersion, schemaVersion, nodeKey, fromMs, toMs, pageSize, continuationToken),
			cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		return WithWeakETag(http, result.Value.ETagBasis, () => FactPageDto.From(result.Value));
	}

	private static async Task<IResult> ListFactStreamsAsync(
		Guid runId, HttpContext http, ListRunFactStreamsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListRunFactStreamsQuery(runId), cancellationToken);
		return result.IsSuccess
			? WithWeakETag(http, result.Value.ETagBasis, () => FactStreamListDto.From(result.Value))
			: ProblemResults.From(result.Failure);
	}

	private static IResult WithWeakETag<T>(HttpContext http, string basis, Func<T> body)
	{
		var etag = $"W/\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(basis)))[..32]}\"";
		http.Response.Headers.ETag = etag;
		var candidates = http.Request.Headers.IfNoneMatch.ToString()
			.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
		return candidates.Any(c => c == "*" || c == etag || c == etag[2..])
			? Results.StatusCode(StatusCodes.Status304NotModified)
			: Results.Ok(body());
	}
}

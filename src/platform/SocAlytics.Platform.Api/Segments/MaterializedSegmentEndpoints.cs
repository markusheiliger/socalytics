using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Api.Segments;

internal sealed record MaterializedSegmentDto(
	Guid RecordingVersionId, string TimelineMappingDigest, string SegmentationPolicyDigest, int SegmentNumber, string State,
	long? MediaSizeBytes, string? FailureReason, bool InUse, DateTimeOffset? AvailableAt);

internal sealed record MaterializedSegmentListDto(Guid MatchId, IReadOnlyList<MaterializedSegmentDto> Segments);

internal sealed record SegmentPurgeResultDto(Guid MatchId, int Purged, int InUse);

internal static class MaterializedSegmentEndpoints
{
	public static IEndpointRouteBuilder MapMaterializedSegmentEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/matches/{matchId:guid}/materialized-segments", ListAsync)
			.WithName("listMaterializedSegments")
			.WithTags("Materialized segments")
			.Produces<MaterializedSegmentListDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/matches/{matchId:guid}/materialized-segments/purge", PurgeAsync)
			.WithName("purgeMaterializedSegments")
			.WithTags("Materialized segments")
			.Produces<SegmentPurgeResultDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		return routes;
	}

	private static async Task<IResult> ListAsync(Guid matchId, ListMaterializedSegmentsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(matchId, cancellationToken);
		return result.IsSuccess
			? Results.Ok(new MaterializedSegmentListDto(
				result.Value.MatchId,
				[.. result.Value.Segments.Select(s => new MaterializedSegmentDto(
					s.RecordingVersionId, s.TimelineMappingDigest, s.SegmentationPolicyDigest, s.SegmentNumber,
					MaterializedSegmentStates.ToPersistedName(s.State), s.MediaSizeBytes, s.FailureReason, s.InUse, s.AvailableAt))]))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> PurgeAsync(Guid matchId, PurgeMaterializedSegmentsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(matchId, cancellationToken);
		return result.IsSuccess
			? Results.Ok(new SegmentPurgeResultDto(result.Value.MatchId, result.Value.Purged, result.Value.InUse))
			: ProblemResults.From(result.Failure);
	}
}

using System.Text.Json;
using System.Text.RegularExpressions;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Commands;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record ClaimRequestDto(Guid ReadinessOccurrenceId, Guid MessageId);

internal sealed record ClaimGrantedDto(
	Guid AttemptId, Guid LogicalJobId, Guid ReadinessOccurrenceId, int AttemptNumber, long FencingToken,
	DateTimeOffset LeaseExpiresAt, int LeaseSeconds, int StaleTimeoutSeconds, int ExecutionTimeoutSeconds, bool Replayed);

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
		return routes;
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

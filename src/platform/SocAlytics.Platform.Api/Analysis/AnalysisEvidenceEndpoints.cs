using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Evidence;
using SocAlytics.Platform.Domain.Analysis.Evidence;

namespace SocAlytics.Platform.Api.Analysis;

internal sealed record EvidenceFactRequestDto(Guid? AcceptedResultId, int? Ordinal, long? ContextBeforeMs, long? ContextAfterMs);

internal sealed record EvidenceRangeRequestDto(long? FromMs, long? ToMs);

internal sealed record ResolveEvidenceMediaRequest(EvidenceFactRequestDto? Fact, EvidenceRangeRequestDto? Range, bool? RetryFailed);

internal sealed record EvidenceRunDto(Guid RunId, Guid MatchId, Guid TeamId);

internal sealed record EvidenceWindowDto(long FromMs, long ToMs);

internal sealed record EvidenceFactWindowDto(long StartMs, long EndMs);

internal sealed record EvidenceMediaDto(
	string Url, string ExpiresAt, string MediaType, string MediaDigest, long MediaSizeBytes, long StartOffsetMs, long EndOffsetMs,
	Guid RecordingVersionId);

internal sealed record EvidencePartDto(
	long StartMs,
	long EndMs,
	string State,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RetryAfterSeconds,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EvidenceMediaDto? Media);

internal sealed record EvidenceMediaResponse(
	EvidenceRunDto Run,
	EvidenceWindowDto Requested,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EvidenceFactWindowDto? Fact,
	bool Shortened,
	IReadOnlyList<EvidencePartDto> Parts)
{
	public static EvidenceMediaResponse From(EvidenceResolution r) => new(
		new EvidenceRunDto(r.RunId, r.MatchId, r.TeamId),
		new EvidenceWindowDto(r.RequestedFromMs, r.RequestedToMs),
		r.FactStartMs is { } start && r.FactEndMs is { } end ? new EvidenceFactWindowDto(start, end) : null,
		r.Shortened,
		[.. r.Parts.Select(p => new EvidencePartDto(
			p.StartMs, p.EndMs, p.State.ToWireName(), p.Reason, p.RetryAfterSeconds,
			p.Media is null
				? null
				: new EvidenceMediaDto(
					p.Media.Url.AbsoluteUri,
					p.Media.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
					p.Media.MediaType, p.Media.MediaDigest, p.Media.MediaSizeBytes, p.Media.StartOffsetMs, p.Media.EndOffsetMs,
					p.Media.RecordingVersionId)))]);
}

internal static class AnalysisEvidenceEndpoints
{
	private const int MaxBodyBytes = 16 * 1024;

	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

	public static IEndpointRouteBuilder MapAnalysisEvidenceEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapPost("/analysis/runs/{runId:guid}/evidence-media", ResolveAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.RequireRateLimiting(EvidenceRateLimiting.Policy)
			.Accepts<ResolveEvidenceMediaRequest>("application/json")
			.WithName("resolveAnalysisEvidenceMedia")
			.WithTags("Analysis evidence")
			.Produces<EvidenceMediaResponse>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status429TooManyRequests)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		return routes;
	}

	private static async Task<IResult> ResolveAsync(
		Guid runId, HttpContext http, ResolveEvidenceMediaHandler handler, CancellationToken cancellationToken)
	{
		http.Response.Headers.CacheControl = "no-store";
		if (http.Request.ContentLength > MaxBodyBytes)
		{
			return ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large");
		}

		ResolveEvidenceMediaRequest? body;
		try
		{
			using var buffer = new MemoryStream();
			var chunk = new byte[4096];
			int read;
			while ((read = await http.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
			{
				buffer.Write(chunk, 0, read);
				if (buffer.Length > MaxBodyBytes)
				{
					return ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large");
				}
			}

			buffer.Position = 0;
			body = await JsonSerializer.DeserializeAsync<ResolveEvidenceMediaRequest>(buffer, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
			body = null;
		}

		if (body is null)
		{
			return ProblemResults.From(OperationFailure.Validation(new FieldViolation("body", "invalid")));
		}

		var command = new ResolveEvidenceMediaCommand(
			runId,
			body.Fact is null ? null : new EvidenceFactRequest(
				body.Fact.AcceptedResultId, body.Fact.Ordinal, body.Fact.ContextBeforeMs, body.Fact.ContextAfterMs),
			body.Range is null ? null : new EvidenceRangeRequest(body.Range.FromMs, body.Range.ToMs),
			body.RetryFailed ?? false);
		var result = await handler.HandleAsync(command, cancellationToken);
		return result.IsSuccess ? Results.Ok(EvidenceMediaResponse.From(result.Value)) : ProblemResults.From(result.Failure);
	}
}

using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Recordings.Commands;
using SocAlytics.Platform.Application.Recordings.Queries;

namespace SocAlytics.Platform.Api.Recordings;

internal static class RecordingEndpoints
{
	public const int MaxBodyBytes = 1024 * 1024;

	private static readonly JsonSerializerOptions BodyOptions = new(JsonSerializerDefaults.Web);

	public static IEndpointRouteBuilder MapRecordingEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapPost("/matches/{matchId:guid}/upload-sessions", StartAsync)
			.AddEndpointFilter<RecordingBodyLimitFilter>()
			.AddEndpointFilter<JsonOnlyFilter>()
			.AddEndpointFilter<ObjectStorageUnavailableFilter>()
			.WithName("startRecordingUpload")
			.WithTags("Recording uploads")
			.Produces<UploadSessionWithGrantsDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status413PayloadTooLarge)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		members.MapGet("/matches/{matchId:guid}/upload-sessions/{uploadSessionId:guid}", GetAsync)
			.WithName("getRecordingUploadSession")
			.WithTags("Recording uploads")
			.Produces<UploadSessionDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		return routes;
	}

	private static async Task<IResult> StartAsync(Guid matchId, HttpContext http, StartRecordingUploadHandler handler, CancellationToken cancellationToken)
	{
		var keyHeader = http.Request.Headers["Idempotency-Key"];
		if (keyHeader.Count == 0)
		{
			return ProblemResults.Problem(SharedProblemCodes.StatusOf(SharedProblemCodes.IdempotencyKeyMissing), SharedProblemCodes.IdempotencyKeyMissing);
		}

		StartUploadRequest? body = null;
		try
		{
			using var buffer = new MemoryStream();
			var chunk = new byte[16 * 1024];
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
			body = await JsonSerializer.DeserializeAsync<StartUploadRequest>(buffer, BodyOptions, cancellationToken);
		}
		catch (JsonException)
		{
		}
		catch (BadHttpRequestException)
		{
			return ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large");
		}

		var command = new StartRecordingUploadCommand(
			matchId,
			keyHeader.Count == 1 ? keyHeader[0] : null,
			body?.Recording?.DisplayName,
			body?.Recording?.Description,
			body?.Recording?.ContentType,
			body?.TotalSizeBytes ?? 0,
			body?.PartSizeBytes ?? 0,
			body?.PartDigests);
		var result = await handler.HandleAsync(command, cancellationToken);
		if (!result.IsSuccess)
		{
			return Failure(result.Failure);
		}

		var value = result.Value;
		http.Response.Headers.CacheControl = "no-store";
		http.Response.Headers.ETag = IfMatchHeader.Format(value.Session.Version);
		return Results.Created(
			$"/api/v1/matches/{matchId}/upload-sessions/{value.Session.Id}",
			new UploadSessionWithGrantsDto(UploadSessionDto.From(value.Session, value.State), PartGrantBatchDto.From(value.Grants)));
	}

	private static async Task<IResult> GetAsync(Guid matchId, Guid uploadSessionId, HttpContext http, GetRecordingUploadSessionHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetRecordingUploadSessionQuery(matchId, uploadSessionId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Session.Version);
		return Results.Ok(UploadSessionDto.From(result.Value.Session, result.Value.State));
	}

	private static IResult Failure(OperationFailure failure) =>
		failure.Code == SharedProblemCodes.IdempotencyKeyReused
			? ProblemResults.Problem(SharedProblemCodes.StatusOf(failure.Code), failure.Code)
			: ProblemResults.From(failure);
}

internal sealed class RecordingBodyLimitFilter : IEndpointFilter
{
	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		var http = context.HttpContext;
		if (http.Request.ContentLength > RecordingEndpoints.MaxBodyBytes)
		{
			return ValueTask.FromResult<object?>(ProblemResults.Problem(StatusCodes.Status413PayloadTooLarge, "payload-too-large"));
		}

		var feature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
		if (feature is { IsReadOnly: false })
		{
			feature.MaxRequestBodySize = RecordingEndpoints.MaxBodyBytes;
		}

		return next(context);
	}
}

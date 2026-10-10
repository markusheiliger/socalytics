using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Api.Endpoints.Club;

internal sealed record SeasonCreateRequest(string? Name);

internal sealed record SeasonDto(
	Guid Id,
	string Name,
	string State,
	DateTimeOffset CreatedAt,
	DateTimeOffset? ActivatedAt,
	DateTimeOffset? ArchivedAt,
	long Version);

internal sealed record SeasonPageDto(IReadOnlyList<SeasonDto> Items, string? ContinuationToken);

internal static class SeasonEndpoints
{
	public static IEndpointRouteBuilder MapSeasonEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/seasons", ListSeasonsAsync)
			.WithName("listSeasons")
			.WithTags("Seasons")
			.Produces<SeasonPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapPost("/seasons", CreateSeasonAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createSeason")
			.WithTags("Seasons")
			.Produces<SeasonDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		members.MapGet("/seasons/{seasonId:guid}", GetSeasonAsync)
			.WithName("getSeason")
			.WithTags("Seasons")
			.Produces<SeasonDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/seasons/{seasonId:guid}/activate", ActivateSeasonAsync)
			.WithName("activateSeason")
			.WithTags("Seasons")
			.Produces<SeasonDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		members.MapPost("/seasons/{seasonId:guid}/archive", ArchiveSeasonAsync)
			.WithName("archiveSeason")
			.WithTags("Seasons")
			.Produces<SeasonDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict);
		return routes;
	}

	private static SeasonDto ToDto(Season s) =>
		new(s.Id, s.Name.Value, s.State.ToWire(), s.CreatedAt, s.ActivatedAt, s.ArchivedAt, s.Version);

	private static IResult Respond(HttpContext http, OperationResult<Season> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
	}

	private static async Task<IResult> ListSeasonsAsync(string? pageSize, string? continuationToken, ListSeasonsHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListSeasonsQuery(pageSize, continuationToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new SeasonPageDto([.. result.Value.Items.Select(ToDto)], result.Value.ContinuationToken))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CreateSeasonAsync(HttpContext http, CreateSeasonHandler handler, CancellationToken cancellationToken)
	{
		SeasonCreateRequest? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<SeasonCreateRequest>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new CreateSeasonCommand(body?.Name), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Created($"/api/v1/seasons/{result.Value.Id}", ToDto(result.Value));
	}

	private static async Task<IResult> GetSeasonAsync(Guid seasonId, HttpContext http, GetSeasonHandler handler, CancellationToken cancellationToken) =>
		Respond(http, await handler.HandleAsync(new GetSeasonQuery(seasonId), cancellationToken));

	private static async Task<IResult> ActivateSeasonAsync(Guid seasonId, HttpContext http, ActivateSeasonHandler handler, CancellationToken cancellationToken) =>
		Respond(http, await handler.HandleAsync(new ActivateSeasonCommand(seasonId), cancellationToken));

	private static async Task<IResult> ArchiveSeasonAsync(Guid seasonId, HttpContext http, ArchiveSeasonHandler handler, CancellationToken cancellationToken) =>
		Respond(http, await handler.HandleAsync(new ArchiveSeasonCommand(seasonId), cancellationToken));
}

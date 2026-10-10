using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Club;
using ClubEntity = SocAlytics.Platform.Domain.Club.Club;

namespace SocAlytics.Platform.Api.Endpoints.Club;

internal sealed record ClubSettingsUpdate(string? DisplayName);

internal sealed record ClubDto(Guid Id, string DisplayName, long Version);

internal static class ClubEndpoints
{
	public static IEndpointRouteBuilder MapClubEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/club", GetClubAsync)
			.WithName("getClub")
			.WithTags("Club")
			.Produces<ClubDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapPut("/club", UpdateClubSettingsAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("updateClubSettings")
			.WithTags("Club")
			.Produces<ClubDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		return routes;
	}

	private static async Task<IResult> GetClubAsync(HttpContext http, GetClubHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetClubQuery(), cancellationToken);
		return result.IsSuccess ? Respond(http, result.Value) : ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> UpdateClubSettingsAsync(HttpContext http, UpdateClubSettingsHandler handler, CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		ClubSettingsUpdate? body;
		try
		{
			body = await http.Request.ReadFromJsonAsync<ClubSettingsUpdate>(cancellationToken);
		}
		catch (System.Text.Json.JsonException)
		{
			body = null;
		}

		var result = await handler.HandleAsync(new UpdateClubSettingsCommand(body?.DisplayName, version.Value), cancellationToken);
		return result.IsSuccess ? Respond(http, result.Value) : ProblemResults.From(result.Failure);
	}

	private static IResult Respond(HttpContext http, ClubEntity club)
	{
		http.Response.Headers.ETag = IfMatchHeader.Format(club.Version);
		return Results.Ok(new ClubDto(club.Id, club.DisplayName.Value, club.Version));
	}
}

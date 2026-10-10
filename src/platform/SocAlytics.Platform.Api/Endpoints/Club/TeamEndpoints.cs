using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Api.Endpoints.Club;

internal sealed record TeamDto(Guid Id, Guid SeasonId, string SeasonState, string Name, DateTimeOffset CreatedAt, long Version);

internal sealed record TeamPageDto(IReadOnlyList<TeamDto> Items, string? ContinuationToken);

internal static class TeamEndpoints
{
	public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/seasons/{seasonId:guid}/teams", ListSeasonTeamsAsync)
			.WithName("listSeasonTeams")
			.WithTags("Teams")
			.Produces<TeamPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/seasons/{seasonId:guid}/teams", CreateTeamAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createTeam")
			.WithTags("Teams")
			.Produces<TeamDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		members.MapGet("/teams", ListTeamsAsync)
			.WithName("listTeams")
			.WithTags("Teams")
			.Produces<TeamPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapGet("/teams/{teamId:guid}", GetTeamAsync)
			.WithName("getTeam")
			.WithTags("Teams")
			.Produces<TeamDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPut("/teams/{teamId:guid}", UpdateTeamAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("updateTeam")
			.WithTags("Teams")
			.Produces<TeamDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status412PreconditionFailed)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
			.ProducesProblem(StatusCodes.Status428PreconditionRequired);
		return routes;
	}

	private static TeamDto ToDto(TeamView v) =>
		new(v.Team.Id, v.Team.SeasonId, v.SeasonState.ToWire(), v.Team.Name.Value, v.Team.CreatedAt, v.Team.Version);

	private static IResult Page(OperationResult<TeamPage> result) =>
		result.IsSuccess
			? Results.Ok(new TeamPageDto([.. result.Value.Items.Select(ToDto)], result.Value.ContinuationToken))
			: ProblemResults.From(result.Failure);

	private static IResult Respond(HttpContext http, OperationResult<TeamView> result)
	{
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Team.Version);
		return Results.Ok(ToDto(result.Value));
	}

	private static async Task<(string? Name, bool HasSeasonId)> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
	{
		try
		{
			using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
			if (doc.RootElement.ValueKind != JsonValueKind.Object)
			{
				return (null, false);
			}

			string? name = null;
			var hasSeasonId = false;
			foreach (var property in doc.RootElement.EnumerateObject())
			{
				if (property.NameEquals("name") && property.Value.ValueKind == JsonValueKind.String)
				{
					name = property.Value.GetString();
				}
				else if (property.Name.Equals("seasonId", StringComparison.OrdinalIgnoreCase))
				{
					hasSeasonId = true;
				}
			}

			return (name, hasSeasonId);
		}
		catch (JsonException)
		{
			return (null, false);
		}
	}

	private static async Task<IResult> ListSeasonTeamsAsync(Guid seasonId, string? pageSize, string? continuationToken, ListTeamsHandler handler, CancellationToken cancellationToken) =>
		Page(await handler.HandleAsync(new ListSeasonTeamsQuery(seasonId, pageSize, continuationToken), cancellationToken));

	private static async Task<IResult> ListTeamsAsync(string? pageSize, string? continuationToken, ListTeamsHandler handler, CancellationToken cancellationToken) =>
		Page(await handler.HandleAsync(new ListTeamsQuery(pageSize, continuationToken), cancellationToken));

	private static async Task<IResult> CreateTeamAsync(Guid seasonId, HttpContext http, CreateTeamHandler handler, CancellationToken cancellationToken)
	{
		var (name, _) = await ReadBodyAsync(http.Request, cancellationToken);
		var result = await handler.HandleAsync(new CreateTeamCommand(seasonId, name), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Team.Version);
		return Results.Created($"/api/v1/teams/{result.Value.Team.Id}", ToDto(result.Value));
	}

	private static async Task<IResult> GetTeamAsync(Guid teamId, HttpContext http, GetTeamHandler handler, CancellationToken cancellationToken) =>
		Respond(http, await handler.HandleAsync(new GetTeamQuery(teamId), cancellationToken));

	private static async Task<IResult> UpdateTeamAsync(Guid teamId, HttpContext http, UpdateTeamHandler handler, CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		var (name, hasSeasonId) = await ReadBodyAsync(http.Request, cancellationToken);
		return Respond(http, await handler.HandleAsync(new UpdateTeamCommand(teamId, name, hasSeasonId, version.Value), cancellationToken));
	}
}

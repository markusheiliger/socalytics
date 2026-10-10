using System.Text.Json;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Club;
using SocAlytics.Platform.Domain.Club;

namespace SocAlytics.Platform.Api.Endpoints.Club;

internal sealed record MatchOpponentDto(string Name);

internal sealed record MatchDto(Guid Id, Guid TeamId, MatchOpponentDto Opponent, DateTimeOffset KickoffAt, string HomeAway, string? Competition, DateTimeOffset CreatedAt, long Version);

internal sealed record MatchPageDto(IReadOnlyList<MatchDto> Items, string? ContinuationToken);

internal static class MatchEndpoints
{
	public static IEndpointRouteBuilder MapMatchEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/teams/{teamId:guid}/matches", ListTeamMatchesAsync)
			.WithName("listTeamMatches")
			.WithTags("Matches")
			.Produces<MatchPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPost("/teams/{teamId:guid}/matches", CreateMatchAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("createMatch")
			.WithTags("Matches")
			.Produces<MatchDto>(StatusCodes.Status201Created)
			.ProducesProblem(StatusCodes.Status400BadRequest)
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound)
			.ProducesProblem(StatusCodes.Status409Conflict)
			.ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
		members.MapGet("/matches/{matchId:guid}", GetMatchAsync)
			.WithName("getMatch")
			.WithTags("Matches")
			.Produces<MatchDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized)
			.ProducesProblem(StatusCodes.Status403Forbidden)
			.ProducesProblem(StatusCodes.Status404NotFound);
		members.MapPut("/matches/{matchId:guid}", UpdateMatchAsync)
			.AddEndpointFilter<JsonOnlyFilter>()
			.WithName("updateMatch")
			.WithTags("Matches")
			.Produces<MatchDto>()
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

	private static MatchDto ToDto(Match m) =>
		new(m.Id, m.TeamId, new MatchOpponentDto(m.Opponent.Name.Value), m.Details.KickoffAt, m.Details.HomeAway.ToWire(), m.Details.Competition, m.CreatedAt, m.Version);

	private static string? String(JsonElement root, string name) =>
		root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

	private static async Task<CreateMatchCommand> ReadBodyAsync(Guid teamId, HttpRequest request, CancellationToken cancellationToken)
	{
		try
		{
			using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
			var root = doc.RootElement;
			if (root.ValueKind != JsonValueKind.Object)
			{
				return new CreateMatchCommand(teamId, null, null, null, null);
			}

			var opponentName = root.TryGetProperty("opponent", out var o) && o.ValueKind == JsonValueKind.Object ? String(o, "name") : null;
			return new CreateMatchCommand(teamId, opponentName, String(root, "kickoffAt"), String(root, "homeAway"), String(root, "competition"));
		}
		catch (JsonException)
		{
			return new CreateMatchCommand(teamId, null, null, null, null);
		}
	}

	private static async Task<IResult> ListTeamMatchesAsync(Guid teamId, string? pageSize, string? continuationToken, ListTeamMatchesHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new ListTeamMatchesQuery(teamId, pageSize, continuationToken), cancellationToken);
		return result.IsSuccess
			? Results.Ok(new MatchPageDto([.. result.Value.Items.Select(ToDto)], result.Value.ContinuationToken))
			: ProblemResults.From(result.Failure);
	}

	private static async Task<IResult> CreateMatchAsync(Guid teamId, HttpContext http, CreateMatchHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(await ReadBodyAsync(teamId, http.Request, cancellationToken), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Created($"/api/v1/matches/{result.Value.Id}", ToDto(result.Value));
	}

	private static async Task<IResult> GetMatchAsync(Guid matchId, HttpContext http, GetMatchHandler handler, CancellationToken cancellationToken)
	{
		var result = await handler.HandleAsync(new GetMatchQuery(matchId), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
	}

	private static async Task<IResult> UpdateMatchAsync(Guid matchId, HttpContext http, UpdateMatchHandler handler, CancellationToken cancellationToken)
	{
		var version = IfMatchHeader.Parse(http.Request);
		if (!version.IsSuccess)
		{
			return ProblemResults.From(version.Failure);
		}

		string? kickoff = null, homeAway = null, competition = null;
		var immutable = new List<string>();
		try
		{
			using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: cancellationToken);
			if (doc.RootElement.ValueKind == JsonValueKind.Object)
			{
				foreach (var p in doc.RootElement.EnumerateObject())
				{
					var str = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
					if (p.Name.Equals("kickoffAt", StringComparison.Ordinal))
					{
						kickoff = str;
					}
					else if (p.Name.Equals("homeAway", StringComparison.Ordinal))
					{
						homeAway = str;
					}
					else if (p.Name.Equals("competition", StringComparison.Ordinal))
					{
						competition = str;
					}
					else if (p.Name.Equals("opponent", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("teamId", StringComparison.OrdinalIgnoreCase))
					{
						immutable.Add(p.Name.Equals("teamId", StringComparison.OrdinalIgnoreCase) ? "teamId" : "opponent");
					}
				}
			}
		}
		catch (JsonException)
		{
		}

		var result = await handler.HandleAsync(new UpdateMatchCommand(matchId, kickoff, homeAway, competition, immutable, version.Value), cancellationToken);
		if (!result.IsSuccess)
		{
			return ProblemResults.From(result.Failure);
		}

		http.Response.Headers.ETag = IfMatchHeader.Format(result.Value.Version);
		return Results.Ok(ToDto(result.Value));
	}
}

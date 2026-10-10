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
			.ProducesProblem(StatusCodes.Status404NotFound);
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
}

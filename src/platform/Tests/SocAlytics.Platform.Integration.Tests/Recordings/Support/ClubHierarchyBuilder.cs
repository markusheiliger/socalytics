using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

namespace SocAlytics.Platform.Integration.Tests.Recordings.Support;

internal sealed record ClubHierarchy(Guid SeasonId, Guid TeamId, Guid MatchId);

// Creates Club hierarchy rows through the Club API as a Club Admin.
internal sealed class ClubHierarchyBuilder(PlatformApiFactory factory, ApiSession admin)
{
	public PlatformApiFactory Factory { get; } = factory;

	public async Task<ClubHierarchy> CreateAsync(CancellationToken cancellationToken)
	{
		var seasonId = await PostAsync("/api/v1/seasons", new { name = $"Season {Guid.NewGuid():N}" }, cancellationToken);
		using (var activated = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{seasonId}/activate", null, cancellationToken))
		{
			activated.StatusCode.ShouldBe(HttpStatusCode.OK, await activated.Content.ReadAsStringAsync(cancellationToken));
		}

		var (teamId, matchId) = await CreateTeamWithMatchAsync(seasonId, cancellationToken);
		return new ClubHierarchy(seasonId, teamId, matchId);
	}

	public async Task<(Guid TeamId, Guid MatchId)> CreateTeamWithMatchAsync(Guid seasonId, CancellationToken cancellationToken)
	{
		var teamId = await PostAsync($"/api/v1/seasons/{seasonId}/teams", new { name = $"Team {Guid.NewGuid():N}" }, cancellationToken);
		return (teamId, await CreateMatchAsync(teamId, cancellationToken));
	}

	public Task<Guid> CreateMatchAsync(Guid teamId, CancellationToken cancellationToken) =>
		PostAsync(
			$"/api/v1/teams/{teamId}/matches",
			new { opponent = new { name = "Rivals" }, kickoffAt = "2026-05-01T10:00:00Z", homeAway = "home", competition = "League" },
			cancellationToken);

	public async Task ArchiveSeasonAsync(Guid seasonId, CancellationToken cancellationToken)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, $"/api/v1/seasons/{seasonId}/archive", null, cancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(cancellationToken));
	}

	private async Task<Guid> PostAsync(string path, object body, CancellationToken cancellationToken)
	{
		using var response = await admin.SendAsync(HttpMethod.Post, path, body, cancellationToken);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(cancellationToken));
		return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("id").GetGuid();
	}
}

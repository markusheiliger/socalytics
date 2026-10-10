namespace SocAlytics.Platform.Host.Tests;

// Copied from specs/20261005-130701-club-identity-foundation/contracts/openapi.yaml.
internal sealed record ContractOperation(string OperationId, string Path, string Method, params string[] StatusCodes);

internal static class ContractOperations
{
	public static readonly IReadOnlyList<ContractOperation> All =
	[
		new("signIn", "/api/v1/session", "POST", "200", "400", "401", "415"),
		new("getSession", "/api/v1/session", "GET", "200", "401"),
		new("signOut", "/api/v1/session", "DELETE", "204", "401", "403"),
		new("getCurrentMember", "/api/v1/me", "GET", "200", "401"),
		new("changeOwnPassword", "/api/v1/me/password", "POST", "204", "400", "401", "403", "415"),
		new("redeemCredential", "/api/v1/credentials/redeem", "POST", "204", "400", "415"),
		new("getClub", "/api/v1/club", "GET", "200", "401", "403"),
		new("updateClubSettings", "/api/v1/club", "PUT", "200", "400", "401", "403", "412", "415", "428"),
		new("listSeasons", "/api/v1/seasons", "GET", "200", "400", "401", "403"),
		new("createSeason", "/api/v1/seasons", "POST", "201", "400", "401", "403", "415"),
		new("getSeason", "/api/v1/seasons/{seasonId}", "GET", "200", "401", "403", "404"),
		new("activateSeason", "/api/v1/seasons/{seasonId}/activate", "POST", "200", "401", "403", "404", "409"),
		new("archiveSeason", "/api/v1/seasons/{seasonId}/archive", "POST", "200", "401", "403", "404", "409"),
		new("listSeasonTeams", "/api/v1/seasons/{seasonId}/teams", "GET", "200", "400", "401", "403", "404"),
		new("createTeam", "/api/v1/seasons/{seasonId}/teams", "POST", "201", "400", "401", "403", "404", "409", "415"),
		new("listTeams", "/api/v1/teams", "GET", "200", "400", "401", "403"),
		new("getTeam", "/api/v1/teams/{teamId}", "GET", "200", "401", "403", "404"),
		new("updateTeam", "/api/v1/teams/{teamId}", "PUT", "200", "400", "401", "403", "404", "409", "412", "415", "428"),
		new("listTeamMatches", "/api/v1/teams/{teamId}/matches", "GET", "200", "400", "401", "403", "404"),
		new("createMatch", "/api/v1/teams/{teamId}/matches", "POST", "201", "400", "401", "403", "404", "409", "415"),
		new("getMatch", "/api/v1/matches/{matchId}", "GET", "200", "401", "403", "404"),
		new("updateMatch", "/api/v1/matches/{matchId}", "PUT", "200", "400", "401", "403", "404", "409", "412", "415", "428"),
		new("listMembers", "/api/v1/members", "GET", "200", "400", "401", "403"),
		new("createMember", "/api/v1/members", "POST", "201", "400", "401", "403", "409", "415"),
		new("getMember", "/api/v1/members/{memberId}", "GET", "200", "401", "403", "404"),
		new("deactivateMember", "/api/v1/members/{memberId}/deactivate", "POST", "200", "401", "403", "404", "409"),
		new("reactivateMember", "/api/v1/members/{memberId}/reactivate", "POST", "200", "401", "403", "404", "409"),
		new("unlockMember", "/api/v1/members/{memberId}/unlock", "POST", "200", "401", "403", "404"),
		new("assignClubRole", "/api/v1/members/{memberId}/club-roles/{clubRole}", "PUT", "200", "401", "403", "404", "409"),
		new("revokeClubRole", "/api/v1/members/{memberId}/club-roles/{clubRole}", "DELETE", "200", "401", "403", "404", "409"),
		new("assignTeamRole", "/api/v1/members/{memberId}/team-roles/{teamId}", "PUT", "200", "400", "401", "403", "404", "409", "415"),
		new("revokeTeamRole", "/api/v1/members/{memberId}/team-roles/{teamId}", "DELETE", "200", "401", "403", "404"),
		new("issueCredential", "/api/v1/members/{memberId}/credentials", "POST", "201", "400", "401", "403", "404", "409", "415"),
		new("endMemberSessions", "/api/v1/members/{memberId}/sessions", "DELETE", "204", "401", "403", "404"),
	];
}

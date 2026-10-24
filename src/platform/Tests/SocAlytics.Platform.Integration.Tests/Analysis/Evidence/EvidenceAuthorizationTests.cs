using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Evidence;

public sealed class EvidenceAuthorizationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static object FactBody(EvidenceScenario s, Guid? resultId = null) =>
		new { fact = new { acceptedResultId = resultId ?? s.ResultId, ordinal = EvidenceScenario.InsideFactOrdinal } };

	private static Task<HttpResponseMessage> ResolveAsync(ApiSession session, string path, object body, CancellationToken ct) =>
		session.SendAsync(HttpMethod.Post, path, body, ct);

	private static async Task ExecAsync(EvidenceScenario s, string sql, object? args, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		foreach (var p in args?.GetType().GetProperties() ?? [])
		{
			command.Parameters.AddWithValue(p.Name, p.GetValue(args)!);
		}

		await command.ExecuteNonQueryAsync(ct);
	}

	[Fact]
	// SC-003, FR-013: Club Admin, Coach, and Viewer are allowed
	public async Task ClubAdminCoachAndViewerAreAllowed()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct);

		foreach (var session in new[] { s.Admin, s.Coach, s.Viewer })
		{
			using var response = await ResolveAsync(session, s.EvidencePath, FactBody(s), ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			response.Headers.CacheControl!.NoStore.ShouldBeTrue();
		}
	}

	[Fact]
	// SC-003, FR-013: members of other teams, members without a role, missing runs, and facts of other runs
	public async Task OutsidersMissingRunsAndForeignFactsReceiveNotFound()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct);

		var (otherTeam, _) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Setup.Hierarchy.SeasonId, ct);
		var otherCoachId = await TestMembers.SeedAsync(s.Setup.Db, "coach-2", cancellationToken: ct);
		await ExecAsync(s,
			"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
			new { m = otherCoachId, t = otherTeam }, ct);
		using var otherCoach = await ApiSession.SignInAsync(s.Factory, "coach-2", TestMembers.DefaultPassword, ct);

		foreach (var session in new[] { otherCoach, s.Outsider })
		{
			using var response = await ResolveAsync(session, s.EvidencePath, FactBody(s), ct);
			response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
			(await response.Content.ReadAsStringAsync(ct)).ShouldNotContain("X-Amz-Signature");
		}

		using var missingRun = await ResolveAsync(s.Coach, $"/api/v1/analysis/runs/{Guid.NewGuid()}/evidence-media", FactBody(s), ct);
		missingRun.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var missingFact = await ResolveAsync(s.Coach, s.EvidencePath, FactBody(s, Guid.NewGuid()), ct);
		missingFact.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var missingOrdinal = await ResolveAsync(
			s.Coach, s.EvidencePath, new { fact = new { acceptedResultId = s.ResultId, ordinal = 99 } }, ct);
		missingOrdinal.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		var otherRun = Guid.NewGuid();
		await ExecAsync(s,
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
			VALUES (@run, @team, @match, @set, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', now(), now(), now());
			INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest)
			VALUES (@run, '{"segmentationPolicy":{"version":"1","durationSeconds":5,"digest":"x"}}'::jsonb, 'sha-256:' || repeat('a', 64));
			""",
			new { run = otherRun, team = s.TeamId, match = s.MatchId, set = Guid.NewGuid() }, ct);
		using var foreignFact = await ResolveAsync(s.Coach, $"/api/v1/analysis/runs/{otherRun}/evidence-media", FactBody(s), ct);
		foreignFact.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	// SC-003, FR-013: authorization is evaluated on every call
	public async Task CoachWhoseRoleIsRevokedReceivesNotFoundWithoutLink()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct);

		using (var first = await ResolveAsync(s.Coach, s.EvidencePath, FactBody(s), ct))
		{
			first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(ct));
		}

		await ExecAsync(s, "DELETE FROM socalytics.team_role_assignment WHERE member_account_id = @m", new { m = s.Setup.CoachId }, ct);

		using var revoked = await ResolveAsync(s.Coach, s.EvidencePath, FactBody(s), ct);
		revoked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		var text = await revoked.Content.ReadAsStringAsync(ct);
		text.ShouldNotContain("\"url\"");
		text.ShouldNotContain("X-Amz-Signature");
	}

	[Fact]
	// SC-003: no session
	public async Task NoSessionReceivesUnauthorized()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await EvidenceScenario.StartAsync(postgres, rustFs, ct);

		using var anonymous = s.Factory.CreateApiClient();
		using var response = await anonymous.PostAsJsonAsync(s.EvidencePath, FactBody(s), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}
}

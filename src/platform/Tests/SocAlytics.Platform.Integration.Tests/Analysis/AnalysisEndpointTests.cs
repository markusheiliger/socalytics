using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class AnalysisEndpointTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static async Task ExecAsync(FinalizationSetup s, string sql, object? args, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(sql, c);
		foreach (var p in args?.GetType().GetProperties() ?? [])
		{
			cmd.Parameters.AddWithValue(p.Name, p.GetValue(args)!);
		}

		await cmd.ExecuteNonQueryAsync(ct);
	}

	private static async Task<Guid> SeedRunAsync(FinalizationSetup s, DateTimeOffset createdAt, CancellationToken ct)
	{
		var run = Guid.NewGuid();
		var node = Guid.NewGuid();
		await ExecAsync(s,
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
			VALUES (@run, @team, @match, @set, 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (@job, @run, @node, 'match', 'Waiting', @at);
			""",
			new { run, node, job = Guid.NewGuid(), team = s.Hierarchy.TeamId, match = s.Hierarchy.MatchId, set = Guid.NewGuid(), at = createdAt },
			ct);
		return run;
	}

	private static async Task<ApiSession> MemberAsync(FinalizationSetup s, string name, string? role, Guid? team, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(s.Db, name, cancellationToken: ct);
		if (role is not null)
		{
			await ExecAsync(s,
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, @r, now(), @m)",
				new { m = id, t = team!.Value, r = role }, ct);
		}

		return await ApiSession.SignInAsync(s.Factory, name, TestMembers.DefaultPassword, ct);
	}

	[Fact]
	public async Task CoachAndViewerReadRunAndPageMatchList()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var now = DateTimeOffset.UtcNow;
		var older = await SeedRunAsync(s, now.AddMinutes(-2), ct);
		var newer = await SeedRunAsync(s, now.AddMinutes(-1), ct);
		var viewer = await MemberAsync(s, "viewer-1", "viewer", s.Hierarchy.TeamId, ct);

		foreach (var session in new[] { s.Coach, viewer })
		{
			using var response = await session.Client.GetAsync($"/api/v1/analysis/runs/{older}", ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			response.Headers.ETag!.IsWeak.ShouldBeTrue();
			var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			body.GetProperty("runId").GetGuid().ShouldBe(older);
			var node = body.GetProperty("nodes")[0];
			node.GetProperty("nodeKey").GetString().ShouldBe("m1");
			node.GetProperty("jobs").GetProperty("waiting").GetInt32().ShouldBe(1);
		}

		using var first = await s.Coach.Client.GetAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/analysis-runs?pageSize=1", ct);
		first.StatusCode.ShouldBe(HttpStatusCode.OK);
		var page = await first.Content.ReadFromJsonAsync<JsonElement>(ct);
		page.GetProperty("items")[0].GetProperty("runId").GetGuid().ShouldBe(newer);
		var token = page.GetProperty("continuationToken").GetString()!;
		using var second = await viewer.Client.GetAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/analysis-runs?pageSize=1&continuationToken={token}", ct);
		var page2 = await second.Content.ReadFromJsonAsync<JsonElement>(ct);
		page2.GetProperty("items")[0].GetProperty("runId").GetGuid().ShouldBe(older);
		page2.GetProperty("continuationToken").ValueKind.ShouldBe(JsonValueKind.Null);

		using var bad = await s.Coach.Client.GetAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/analysis-runs?pageSize=201", ct);
		bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
	}

	[Fact]
	public async Task OtherTeamGetsNotFoundAndAnonymousGetsUnauthorized()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, DateTimeOffset.UtcNow, ct);
		var (otherTeam, _) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var outsider = await MemberAsync(s, "coach-2", "coach", otherTeam, ct);

		using var hidden = await outsider.Client.GetAsync($"/api/v1/analysis/runs/{run}", ct);
		hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var missing = await outsider.Client.GetAsync($"/api/v1/analysis/runs/{Guid.NewGuid()}", ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var list = await outsider.Client.GetAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/analysis-runs", ct);
		list.StatusCode.ShouldBe(HttpStatusCode.NotFound);

		using var anonymous = s.Factory.CreateApiClient();
		using var unauth = await anonymous.GetAsync($"/api/v1/analysis/runs/{run}", ct);
		unauth.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task IfNoneMatchReturnsNotModifiedUntilReadinessChanges()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, DateTimeOffset.UtcNow, ct);
		var path = $"/api/v1/analysis/runs/{run}";

		using var ok = await s.Coach.Client.GetAsync(path, ct);
		var etag = ok.Headers.ETag!.ToString();

		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.TryAddWithoutValidation("If-None-Match", etag);
		using var notModified = await s.Coach.Client.SendAsync(request, ct);
		notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);

		await ExecAsync(s, "UPDATE socalytics.analysis_logical_jobs SET state = 'Ready' WHERE run_id = @run", new { run }, ct);
		using var changedRequest = new HttpRequestMessage(HttpMethod.Get, path);
		changedRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
		using var changed = await s.Coach.Client.SendAsync(changedRequest, ct);
		changed.StatusCode.ShouldBe(HttpStatusCode.OK);
		changed.Headers.ETag!.ToString().ShouldNotBe(etag);
	}

	[Fact]
	public async Task OpenApiDocumentContainsAnalysisOperations()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		using var anonymous = s.Factory.CreateApiClient();
		var text = await anonymous.GetStringAsync("/openapi/v1.json", ct);
		text.ShouldContain("\"getAnalysisRun\"");
		text.ShouldContain("\"listMatchAnalysisRuns\"");
	}
}

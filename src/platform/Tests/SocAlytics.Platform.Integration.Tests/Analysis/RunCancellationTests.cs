using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class RunCancellationTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string Reason = "Wrong recordings were finalized.";

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

	private static async Task<Guid> SeedRunAsync(FinalizationSetup s, string state, CancellationToken ct)
	{
		var run = Guid.NewGuid();
		var node = Guid.NewGuid();
		var terminal = state is "Completed" or "PartiallyCompleted" or "Failed";
		var at = DateTimeOffset.UtcNow.AddMinutes(-1);
		await ExecAsync(s,
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, failure_reason, created_at, running_at, terminated_at, updated_at)
			VALUES (@run, @team, @match, @set, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', @state, @failure, @at, @runningAt, @terminatedAt, @at);
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (@job, @run, @node, 'match', 'Waiting', @at);
			""",
			new
			{
				run, node, job = Guid.NewGuid(), state, team = s.Hierarchy.TeamId, match = s.Hierarchy.MatchId, set = Guid.NewGuid(), at,
				failure = state == "Failed" ? "capability-failed" : (object)DBNull.Value,
				runningAt = state == "Requested" ? (object)DBNull.Value : at,
				terminatedAt = terminal ? at : (object)DBNull.Value,
			},
			ct);
		return run;
	}

	private static async Task<ApiSession> MemberAsync(FinalizationSetup s, string name, string role, Guid team, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(s.Db, name, cancellationToken: ct);
		await ExecAsync(s,
			"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, @r, now(), @m)",
			new { m = id, t = team, r = role }, ct);
		return await ApiSession.SignInAsync(s.Factory, name, TestMembers.DefaultPassword, ct);
	}

	private static Task<HttpResponseMessage> CancelAsync(ApiSession session, Guid run, string? ifMatch, object? body, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/runs/{run}/cancellation");
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		return session.Client.SendAsync(request, ct);
	}

	private static async Task<long> VersionAsync(FinalizationSetup s, Guid run, CancellationToken ct) =>
		await s.ScalarAsync($"SELECT version FROM socalytics.analysis_runs WHERE id = '{run}'", ct);

	private static async Task<string> StateAsync(FinalizationSetup s, string table, Guid run, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(
			$"SELECT state FROM socalytics.{table} WHERE {(table == "analysis_runs" ? "id" : "run_id")} = @run", c);
		cmd.Parameters.AddWithValue("run", run);
		return (string)(await cmd.ExecuteScalarAsync(ct))!;
	}

	private static Task<long> AuditCountAsync(FinalizationSetup s, Guid run, CancellationToken ct) =>
		s.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run-cancelled' AND resource_id = '{run}'", ct);

	private static Task<long> StateChangedCountAsync(FinalizationSetup s, Guid run, CancellationToken ct) =>
		s.ScalarAsync($"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = '{run}'", ct);

	private static async Task AssertUnchangedAsync(FinalizationSetup s, Guid run, string state, long version, CancellationToken ct)
	{
		(await StateAsync(s, "analysis_runs", run, ct)).ShouldBe(state);
		(await VersionAsync(s, run, ct)).ShouldBe(version);
		(await AuditCountAsync(s, run, ct)).ShouldBe(0);
		(await StateChangedCountAsync(s, run, ct)).ShouldBe(0);
	}

	[Theory]
	[InlineData("Requested", true)]
	[InlineData("Running", false)]
	public async Task ClubAdminAndCoachCancelUnfinishedRuns(string initial, bool asAdmin)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, initial, ct);
		var session = asAdmin ? s.Admin : s.Coach;
		var accountName = asAdmin ? "admin-1" : "coach-1";
		var version = await VersionAsync(s, run, ct);

		using var response = await CancelAsync(session, run, $"\"{version}\"", new { reason = $"  {Reason}  " }, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);

		var newVersion = await VersionAsync(s, run, ct);
		newVersion.ShouldBeGreaterThan(version);
		AssertCancelled(body, accountName, newVersion);

		(await StateAsync(s, "analysis_runs", run, ct)).ShouldBe("Cancelled");

		using var read = await s.Coach.Client.GetAsync($"/api/v1/analysis/runs/{run}", ct);
		AssertCancelled(await read.Content.ReadFromJsonAsync<JsonElement>(ct), accountName, newVersion);
		using var list = await s.Coach.Client.GetAsync($"/api/v1/matches/{s.Hierarchy.MatchId}/analysis-runs", ct);
		var item = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray()
			.Single(i => i.GetProperty("runId").GetGuid() == run);
		AssertCancelled(item, accountName, newVersion);

		await AssertSingleNotificationAndAuditAsync(s, run, initial, ct);
	}

	private static void AssertCancelled(JsonElement body, string accountName, long version)
	{
		body.GetProperty("state").GetString().ShouldBe("Cancelled");
		body.GetProperty("terminatedAt").ValueKind.ShouldBe(JsonValueKind.String);
		body.GetProperty("cancelledBy").GetProperty("accountName").GetString().ShouldBe(accountName);
		body.GetProperty("cancelledBy").GetProperty("memberId").GetGuid().ShouldNotBe(Guid.Empty);
		body.GetProperty("cancellationReason").GetString().ShouldBe(Reason);
		body.GetProperty("version").GetInt64().ShouldBe(version);
	}

	private async Task AssertSingleNotificationAndAuditAsync(FinalizationSetup s, Guid run, string previous, CancellationToken ct)
	{
		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await using (var cmd = new NpgsqlCommand(
			"SELECT payload::text FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = @run", c))
		{
			cmd.Parameters.AddWithValue("run", run);
			await using var reader = await cmd.ExecuteReaderAsync(ct);
			(await reader.ReadAsync(ct)).ShouldBeTrue();
			var text = reader.GetString(0);
			(await reader.ReadAsync(ct)).ShouldBeFalse();
			var payload = JsonDocument.Parse(text).RootElement;
			payload.GetProperty("state").GetString().ShouldBe("Cancelled");
			payload.GetProperty("previousState").GetString().ShouldBe(previous);
			payload.TryGetProperty("failureReason", out _).ShouldBeFalse();
			text.ShouldNotContain(Reason);
			var validation = s.Factory.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.RunStateChangedV1, payload);
			validation.IsValid.ShouldBeTrue(string.Join(", ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));
		}

		await using var audit = new NpgsqlCommand(
			"SELECT outcome, details::text FROM socalytics.security_audit_event WHERE event_type = 'analysis.run-cancelled' AND resource_id = @run", c);
		audit.Parameters.AddWithValue("run", run.ToString());
		await using var rows = await audit.ExecuteReaderAsync(ct);
		(await rows.ReadAsync(ct)).ShouldBeTrue();
		rows.GetString(0).ShouldBe("succeeded");
		var details = rows.GetString(1);
		details.ShouldNotContain(Reason);
		var detailValues = JsonDocument.Parse(details).RootElement;
		detailValues.GetProperty("runId").GetString().ShouldBe(run.ToString());
		detailValues.GetProperty("matchId").GetString().ShouldBe(s.Hierarchy.MatchId.ToString());
		(await rows.ReadAsync(ct)).ShouldBeFalse();
	}

	[Fact]
	public async Task RepeatsWithCurrentOrPreviousVersionAreIdempotent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, "Running", ct);
		var before = await VersionAsync(s, run, ct);

		using var first = await CancelAsync(s.Coach, run, $"\"{before}\"", new { reason = Reason }, ct);
		first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync(ct));
		var current = await VersionAsync(s, run, ct);

		foreach (var version in new[] { before, current })
		{
			using var repeat = await CancelAsync(s.Admin, run, $"\"{version}\"", new { reason = "Another reason." }, ct);
			repeat.StatusCode.ShouldBe(HttpStatusCode.OK, await repeat.Content.ReadAsStringAsync(ct));
			var body = await repeat.Content.ReadFromJsonAsync<JsonElement>(ct);
			AssertCancelled(body, "coach-1", current);
		}

		(await VersionAsync(s, run, ct)).ShouldBe(current);
		(await AuditCountAsync(s, run, ct)).ShouldBe(1);
		(await StateChangedCountAsync(s, run, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ConcurrentCancellationsWithTheSameVersionRecordOneAuditEventAndOneNotification()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, "Running", ct);
		var version = await VersionAsync(s, run, ct);

		var responses = await Task.WhenAll(
			CancelAsync(s.Coach, run, $"\"{version}\"", new { reason = Reason }, ct),
			CancelAsync(s.Admin, run, $"\"{version}\"", new { reason = Reason }, ct),
			CancelAsync(s.Coach, run, $"\"{version}\"", new { reason = Reason }, ct));
		try
		{
			responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
		}
		finally
		{
			foreach (var response in responses)
			{
				response.Dispose();
			}
		}

		(await AuditCountAsync(s, run, ct)).ShouldBe(1);
		(await StateChangedCountAsync(s, run, ct)).ShouldBe(1);
		(await StateAsync(s, "analysis_runs", run, ct)).ShouldBe("Cancelled");
	}

	[Fact]
	public async Task RefusalsLeaveTheRunUnchanged()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, "Running", ct);
		var version = await VersionAsync(s, run, ct);
		var viewer = await MemberAsync(s, "viewer-1", "viewer", s.Hierarchy.TeamId, ct);
		var (otherTeam, _) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var outsider = await MemberAsync(s, "coach-2", "coach", otherTeam, ct);

		async Task ExpectAsync(ApiSession session, Guid id, string? ifMatch, object? body, HttpStatusCode status)
		{
			using var response = await CancelAsync(session, id, ifMatch, body, ct);
			response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync(ct));
		}

		await ExpectAsync(viewer, run, $"\"{version}\"", null, HttpStatusCode.Forbidden);
		await ExpectAsync(outsider, run, $"\"{version}\"", null, HttpStatusCode.NotFound);
		await ExpectAsync(s.Coach, Guid.NewGuid(), $"\"{version}\"", null, HttpStatusCode.NotFound);
		await ExpectAsync(s.Coach, run, null, null, HttpStatusCode.PreconditionRequired);
		await ExpectAsync(s.Coach, run, $"\"{version + 1}\"", null, HttpStatusCode.PreconditionFailed);
		await ExpectAsync(s.Coach, run, $"\"{version}\"", new { reason = new string('x', 501) }, HttpStatusCode.BadRequest);
		await ExpectAsync(s.Coach, run, $"\"{version}\"", new { reason = "bell\u0007" }, HttpStatusCode.BadRequest);
		await ExpectAsync(s.Coach, run, $"\"{version}\"", new { reason = "tab\there" }, HttpStatusCode.BadRequest);

		using var anonymous = s.Factory.CreateApiClient();
		using var unauthenticated = await anonymous.PostAsync($"/api/v1/analysis/runs/{run}/cancellation", null, ct);
		unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

		await AssertUnchangedAsync(s, run, "Running", version, ct);
		(await StateAsync(s, "analysis_logical_jobs", run, ct)).ShouldBe("Waiting");

		// Line breaks and an empty reason are accepted.
		var ok = await SeedRunAsync(s, "Requested", ct);
		using var accepted = await CancelAsync(s.Coach, ok, $"\"{await VersionAsync(s, ok, ct)}\"", new { reason = "a\r\nb\nc" }, ct);
		accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(ct));
		var empty = await SeedRunAsync(s, "Requested", ct);
		using var noReason = await CancelAsync(s.Coach, empty, $"\"{await VersionAsync(s, empty, ct)}\"", new { reason = "   " }, ct);
		noReason.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await noReason.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("cancellationReason").ValueKind.ShouldBe(JsonValueKind.Null);
	}

	[Theory]
	[InlineData("Completed")]
	[InlineData("PartiallyCompleted")]
	[InlineData("Failed")]
	public async Task TerminalRunsAreRefusedWithConflict(string state)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, state, ct);
		var version = await VersionAsync(s, run, ct);

		foreach (var ifMatch in new[] { $"\"{version}\"", $"\"{version + 5}\"" })
		{
			using var response = await CancelAsync(s.Coach, run, ifMatch, new { reason = Reason }, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(ct));
			var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
			problem.GetProperty("code").GetString().ShouldBe("analysis-run-terminal");
		}

		await AssertUnchangedAsync(s, run, state, version, ct);
		(await StateAsync(s, "analysis_logical_jobs", run, ct)).ShouldBe("Waiting");
	}

	[Fact]
	public async Task CancellationRacingTheRunsOwnTerminalTransitionKeepsTheFirstCommittedState()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var run = await SeedRunAsync(s, "Running", ct);
		var version = await VersionAsync(s, run, ct);

		await ExecAsync(s,
			"UPDATE socalytics.analysis_runs SET state = 'Completed', terminated_at = now() WHERE id = @run", new { run }, ct);
		var completedVersion = await VersionAsync(s, run, ct);

		using var late = await CancelAsync(s.Coach, run, $"\"{version}\"", new { reason = Reason }, ct);
		late.StatusCode.ShouldBe(HttpStatusCode.Conflict);
		(await late.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("analysis-run-terminal");
		await AssertUnchangedAsync(s, run, "Completed", completedVersion, ct);

		var cancelled = await SeedRunAsync(s, "Running", ct);
		using var first = await CancelAsync(s.Coach, cancelled, $"\"{await VersionAsync(s, cancelled, ct)}\"", null, ct);
		first.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await StateAsync(s, "analysis_runs", cancelled, ct)).ShouldBe("Cancelled");
		(await StateChangedCountAsync(s, cancelled, ct)).ShouldBe(1);
	}
}

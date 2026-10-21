using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
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
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
			VALUES (@run, @team, @match, @set, now(), 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
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

	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	private static string NodeSnapshot(string key) =>
		$$"""
		{"nodeKey":"{{key}}","capabilityId":"cap.{{key}}","schemaVersion":"1.0.0","analystProfileId":"profile-{{key}}",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":"{{DigestA}}"}
		""";

	private sealed record CoverageRun(Guid RunId, Guid[] SegmentResults);

	// Segment node "seg" (two accepted jobs, one failed) feeds the match nodes "partial" (partial edge), "full" (complete edge),
	// and "pending" (no input snapshot yet).
	private static async Task<CoverageRun> SeedCoverageRunAsync(FinalizationSetup s, Guid recordingSet, CancellationToken ct)
	{
		var seg = Guid.CreateVersion7();
		var partial = Guid.CreateVersion7();
		var full = Guid.CreateVersion7();
		var pending = Guid.CreateVersion7();
		WorkflowNodeRecord Node(Guid id, string key, CapabilityTier tier, ExecutionScope scope, int ordinal) =>
			new(id, key, $"cap.{key}", "1.0.0", tier, scope, ordinal, WorkflowNodeState.Active, null, default, NodeSnapshot(key));
		var run = new NewAnalysisRun(
			s.Hierarchy.TeamId, s.Hierarchy.MatchId, recordingSet, DateTimeOffset.UtcNow, "wf", "1", ["cap.partial"], "all-requested-capabilities",
			AnalysisRunState.Running, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[
				Node(seg, "seg", CapabilityTier.LowLevel, ExecutionScope.Segment, 0),
				Node(partial, "partial", CapabilityTier.HighLevel, ExecutionScope.Match, 1),
				Node(full, "full", CapabilityTier.HighLevel, ExecutionScope.Match, 2),
				Node(pending, "pending", CapabilityTier.HighLevel, ExecutionScope.Match, 3),
			],
			[
				new DependencyEdgeRecord(seg, partial, DependencyKind.Required, null) { CoveragePolicy = CoveragePolicy.Partial() },
				new DependencyEdgeRecord(seg, full, DependencyKind.Required, null),
				new DependencyEdgeRecord(seg, pending, DependencyKind.Required, null) { CoveragePolicy = CoveragePolicy.Partial() },
			]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var segJobs = new[] { 1, 2, 3 }.Select(n => new NewLogicalJob(
			Guid.CreateVersion7(), default, seg, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, n, LogicalJobState.Waiting, snapshot)).ToArray();
		NewLogicalJob MatchJob(Guid node) =>
			new(Guid.CreateVersion7(), default, node, ExecutionScope.Match, null, null, null, null, LogicalJobState.Waiting, snapshot);
		var partialJob = MatchJob(partial);
		var fullJob = MatchJob(full);
		var pendingJob = MatchJob(pending);

		Guid runId;
		await using (var scope = s.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>()
				.InsertAsync([.. segJobs.Append(partialJob).Append(fullJob).Append(pendingJob).Select(j => j with { RunId = runId })], ct);
			await uow.CommitAsync(ct);
		}

		async Task<Guid> AcceptAsync(NewLogicalJob job, Guid node, string capability)
		{
			var occurrence = Guid.NewGuid();
			var attempt = Guid.NewGuid();
			await ExecAsync(s,
				"""
				INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occurrence, @jobId, 1, 'initial', now());
				INSERT INTO socalytics.analysis_execution_attempts
					(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
				VALUES (@attempt, @jobId, @occurrence, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Stale', now(), now(), now());
				""",
				new { occurrence, attempt, jobId = job.Id }, ct);
			var result = new AcceptedResultRecord(
				Guid.NewGuid(), job.Id, runId, node, attempt, "results/key", DigestA, capability, "1.0.0", "1.0.0",
				DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
			await using var scope = s.Factory.Services.CreateAsyncScope();
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await uow.CommitAsync(ct);
			return result.Id;
		}

		var results = new[] { await AcceptAsync(segJobs[0], seg, "cap.seg"), await AcceptAsync(segJobs[1], seg, "cap.seg") };
		await AcceptAsync(partialJob, partial, "cap.partial");
		await ExecAsync(s, "UPDATE socalytics.analysis_logical_jobs SET state = 'Completed' WHERE id = ANY(@ids)", new { ids = new[] { segJobs[0].Id, segJobs[1].Id, partialJob.Id } }, ct);
		await ExecAsync(s,
			"UPDATE socalytics.analysis_logical_jobs SET state = 'Failed', failure_reason = 'attempts-exhausted' WHERE id = @id",
			new { id = segJobs[2].Id }, ct);

		var partialInputs = JsonSerializer.Serialize(new[]
		{
			new
			{
				upstreamNodeKey = "seg", edgeKind = "required", status = "partial", acceptedResultIds = results,
				coverageMode = "partial", minimumAcceptedFraction = (decimal?)null,
				coverage = new[] { "a", "b", "c" }.Select((id, i) => new { segmentId = id, state = i == 2 ? "failed" : "accepted" }).ToArray(),
			},
		});
		var fullInputs = JsonSerializer.Serialize(new[]
		{
			new { upstreamNodeKey = "seg", edgeKind = "required", status = "accepted", acceptedResultIds = results },
		});
		const string insertInputs = "INSERT INTO socalytics.analysis_logical_job_inputs (logical_job_id, inputs, recorded_at) VALUES (@job, @inputs::jsonb, now())";
		await ExecAsync(s, insertInputs, new { job = partialJob.Id, inputs = partialInputs }, ct);
		await ExecAsync(s, insertInputs, new { job = fullJob.Id, inputs = fullInputs }, ct);
		return new CoverageRun(runId, results);
	}

	[Fact]
	public async Task NodeInputCoverageReportsCountsAndNullBeforeSnapshotAndHidesFromOthers()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var set = await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
		var seeded = await SeedCoverageRunAsync(s, set.RecordingSetVersionId, ct);
		var viewer = await MemberAsync(s, "viewer-1", "viewer", s.Hierarchy.TeamId, ct);

		static int[] Counts(JsonElement e) =>
			[.. new[] { "accepted", "failed", "blocked", "skipped", "missing" }.Select(k => e.GetProperty(k).GetInt32())];

		foreach (var session in new[] { s.Coach, viewer })
		{
			using var response = await session.Client.GetAsync($"/api/v1/analysis/runs/{seeded.RunId}", ct);
			response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
			var nodes = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("nodes").EnumerateArray()
				.ToDictionary(n => n.GetProperty("nodeKey").GetString()!);

			nodes["seg"].GetProperty("inputCoverage").ValueKind.ShouldBe(JsonValueKind.Null);
			nodes["pending"].GetProperty("inputCoverage").ValueKind.ShouldBe(JsonValueKind.Null);

			var partial = nodes["partial"].GetProperty("inputCoverage").EnumerateArray().ToList();
			partial.Count.ShouldBe(1);
			partial[0].GetProperty("upstreamNodeKey").GetString().ShouldBe("seg");
			partial[0].GetProperty("coverageMode").GetString().ShouldBe("partial");
			partial[0].GetProperty("minimumAcceptedFraction").ValueKind.ShouldBe(JsonValueKind.Null);
			Counts(partial[0]).ShouldBe([2, 1, 0, 0, 0]);

			var full = nodes["full"].GetProperty("inputCoverage").EnumerateArray().ToList();
			full.Count.ShouldBe(1);
			full[0].GetProperty("coverageMode").GetString().ShouldBe("complete");
			Counts(full[0]).ShouldBe([2, 0, 0, 0, 0]);
		}

		// The member fact reads keep their semantics: the partial node's own coverage is one accepted match window.
		using var streams = await s.Coach.Client.GetAsync($"/api/v1/analysis/runs/{seeded.RunId}/fact-streams", ct);
		streams.StatusCode.ShouldBe(HttpStatusCode.OK, await streams.Content.ReadAsStringAsync(ct));
		var partialStream = (await streams.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items").EnumerateArray()
			.Single(i => i.GetProperty("nodeKey").GetString() == "partial");
		var coverage = partialStream.GetProperty("coverage");
		coverage.GetProperty("accepted").GetInt32().ShouldBe(1);
		foreach (var other in new[] { "missing", "failed", "blocked", "skipped" })
		{
			coverage.GetProperty(other).GetInt32().ShouldBe(0);
		}

		var (otherTeam, _) = await new ClubHierarchyBuilder(s.Factory, s.Admin).CreateTeamWithMatchAsync(s.Hierarchy.SeasonId, ct);
		var outsider = await MemberAsync(s, "coach-2", "coach", otherTeam, ct);
		using var hidden = await outsider.Client.GetAsync($"/api/v1/analysis/runs/{seeded.RunId}", ct);
		hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
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
		foreach (var operation in new[] { "listOutboxMessages", "getOutboxMessage", "getAnalysisOperationalSummary", "listNotificationReceipts", "reconcileAnalysisPublication" })
		{
			text.ShouldContain($"\"{operation}\"");
		}
	}

	[Fact]
	public async Task ClubAdminInspectsOutboxAndSummaryAndCoachIsForbidden()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var id = Guid.NewGuid();
		await ExecAsync(s,
			"""
			INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, state, attempt_count, publication_round, failure_category, created_at)
			VALUES (@id, 'analysis.ready-work', '1.0.0', 'analysis.ready-work', '{"secret":"x"}'::jsonb, @id, 'failed', 3, 1, 'transport-unavailable', now() - interval '1 minute')
			""", new { id }, ct);

		using var list = await s.Admin.Client.GetAsync("/api/v1/operations/outbox-messages?state=failed", ct);
		list.StatusCode.ShouldBe(HttpStatusCode.OK);
		var text = await list.Content.ReadAsStringAsync(ct);
		text.ShouldNotContain("secret");
		var item = JsonDocument.Parse(text).RootElement.GetProperty("items")[0];
		item.GetProperty("messageId").GetGuid().ShouldBe(id);
		item.GetProperty("failureCategory").GetString().ShouldBe("transport-unavailable");
		using var one = await s.Admin.Client.GetAsync($"/api/v1/operations/outbox-messages/{id}", ct);
		one.StatusCode.ShouldBe(HttpStatusCode.OK);
		using var summary = await s.Admin.Client.GetAsync("/api/v1/operations/analysis/summary", ct);
		summary.StatusCode.ShouldBe(HttpStatusCode.OK);
		(await summary.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("outboxFailed").GetInt32().ShouldBe(1);
		using var receipts = await s.Admin.Client.GetAsync("/api/v1/operations/analysis/notification-receipts", ct);
		receipts.StatusCode.ShouldBe(HttpStatusCode.OK);

		foreach (var path in new[] { "outbox-messages", $"outbox-messages/{id}", "analysis/summary", "analysis/notification-receipts" })
		{
			using var denied = await s.Coach.Client.GetAsync($"/api/v1/operations/{path}", ct);
			denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		}
	}

	[Fact]
	public async Task ReconciliationRequiresAntiforgeryAndWritesOneAuditEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		const string path = "/api/v1/operations/analysis/reconciliations";

		using var missing = await s.Admin.Client.PostAsync(path, null, ct);
		missing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
		(await missing.Content.ReadAsStringAsync(ct)).ShouldContain("antiforgery-failed");
		using var coach = new HttpRequestMessage(HttpMethod.Post, path);
		coach.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
		using var denied = await s.Coach.Client.SendAsync(coach, ct);
		denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

		using var request = new HttpRequestMessage(HttpMethod.Post, path);
		request.Headers.Add("X-CSRF-Token", s.Admin.AntiforgeryToken);
		using var ok = await s.Admin.Client.SendAsync(request, ct);
		ok.StatusCode.ShouldBe(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync(ct));
		(await ok.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("readyJobsRepublished").GetInt32().ShouldBe(0);

		await using var c = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await c.OpenAsync(ct);
		await using var cmd = new NpgsqlCommand(
			"SELECT resource_type, details::text FROM socalytics.security_audit_event WHERE event_type = 'analysis.publication-reconciled'", c);
		await using var reader = await cmd.ExecuteReaderAsync(ct);
		(await reader.ReadAsync(ct)).ShouldBeTrue();
		reader.GetString(0).ShouldBe("club");
		var details = JsonDocument.Parse(reader.GetString(1)).RootElement;
		details.EnumerateObject().Select(p => p.Name).Order().ToArray().ShouldBe(["notificationsRepublished", "readyJobsRepublished"]);
		(await reader.ReadAsync(ct)).ShouldBeFalse();
	}
}

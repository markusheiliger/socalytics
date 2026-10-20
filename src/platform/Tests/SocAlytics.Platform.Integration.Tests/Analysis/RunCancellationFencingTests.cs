using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Messaging;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class RunCancellationFencingTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
	private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	private sealed record Seeded(
		Guid Run, Guid DoneNode, Guid ActiveNode, Guid WaitingNode, Guid ClaimedNode, Guid DoneJob, Guid ReadyJob, Guid ReadyOccurrence, Guid ClaimedJob,
		Guid ClaimedOccurrence, Guid WaitingJob, Guid[] ReadyWorkMessages);

	private static async Task<NpgsqlConnection> OpenAsync(IsolatedDatabase db, CancellationToken ct)
	{
		var c = new NpgsqlConnection(db.MigratorConnectionString);
		await c.OpenAsync(ct);
		return c;
	}

	private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		return await c.ExecuteScalarAsync<long>(new CommandDefinition(sql, cancellationToken: ct));
	}

	private static async Task<string> SnapshotAsync(IsolatedDatabase db, Guid run, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		var queries = new[]
		{
			$"SELECT * FROM socalytics.analysis_runs WHERE id = '{run}' ORDER BY id",
			$"SELECT * FROM socalytics.analysis_workflow_nodes WHERE run_id = '{run}' ORDER BY id",
			$"SELECT * FROM socalytics.analysis_logical_jobs WHERE run_id = '{run}' ORDER BY id",
			$"SELECT a.* FROM socalytics.analysis_execution_attempts a JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id WHERE j.run_id = '{run}' ORDER BY a.id",
			$"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{run}' ORDER BY id",
			$"SELECT * FROM socalytics.outbox_messages WHERE run_id = '{run}' ORDER BY id",
		};
		var parts = new List<string>();
		foreach (var query in queries)
		{
			var rows = await c.QueryAsync<string>(new CommandDefinition($"SELECT row_to_json(t)::text FROM ({query}) t", cancellationToken: ct));
			parts.Add(string.Join("\n", rows));
		}

		return string.Join("\n--\n", parts);
	}

	private async Task<(AnalysisTestHost Host, IsolatedDatabase Db, ApiSession Coach, ClubHierarchy Hierarchy)> StartAsync(CancellationToken ct)
	{
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var host = new AnalysisTestHost(db, nats, rustFs, time: new FakeTimeProvider(DateTimeOffset.UtcNow));
		await host.StartAsync(ct);
		await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
		var coachId = await TestMembers.SeedAsync(db, "coach-1", cancellationToken: ct);
		var admin = await ApiSession.SignInAsync(host.Factory, "admin-1", TestMembers.DefaultPassword, ct);
		var hierarchy = await new ClubHierarchyBuilder(host.Factory, admin).CreateAsync(ct);
		await using (var c = await OpenAsync(db, ct))
		{
			await c.ExecuteAsync(new CommandDefinition(
				"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES (@m, @t, 'coach', now(), @m)",
				new { m = coachId, t = hierarchy.TeamId }, cancellationToken: ct));
		}

		var coach = await ApiSession.SignInAsync(host.Factory, "coach-1", TestMembers.DefaultPassword, ct);
		return (host, db, coach, hierarchy);
	}

	private static async Task<Seeded> SeedRunAsync(
		AnalysisTestHost host, IsolatedDatabase db, ClubHierarchy hierarchy, Guid recordingSet, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		var resolved = (WorkflowResolution.ResolvedResult)await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct);
		var s = new Seeded(
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
			Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), [Guid.NewGuid(), Guid.NewGuid()]);
		var at = DateTimeOffset.UtcNow;
		await using var c = await OpenAsync(db, ct);
		await c.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
			VALUES (@run, @team, @match, @set, @wf, @wfv, ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
			INSERT INTO socalytics.analysis_run_snapshots (run_id, snapshot, snapshot_digest) VALUES (@run, '{}'::jsonb, 'sha-256:a');
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@n0, @run, 'm0', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at),
			       (@n1, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 1, 'Active', @at),
			       (@n2, @run, 'm2', 'cap-a', '1.0.0', 'high-level', 'match', 2, 'Waiting', @at),
			       (@n3, @run, 'm3', 'cap-a', '1.0.0', 'high-level', 'match', 3, 'Active', @at);
			INSERT INTO socalytics.analysis_node_snapshots (node_id, run_id, snapshot)
			VALUES (@n0, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb),
			       (@n1, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb),
			       (@n2, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb),
			       (@n3, @run, '{"analystProfileId":"profile","policyVersions":{}}'::jsonb);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (@done, @run, @n0, 'match', 'Waiting', @at), (@ready, @run, @n1, 'match', 'Waiting', @at),
			       (@claimed, @run, @n3, 'match', 'Waiting', @at), (@waiting, @run, @n2, 'match', 'Waiting', @at);
			INSERT INTO socalytics.analysis_logical_job_snapshots (logical_job_id, max_attempts, lease_seconds, stale_timeout_seconds, execution_timeout_seconds, analyst_profile_digest, image_digest, model_digest, resources, schema_version)
			SELECT id, 2, 30, 90, 600, @d, @d, @d, '{}'::jsonb, '1.0.0' FROM socalytics.analysis_logical_jobs WHERE run_id = @run;
			INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at)
			VALUES (@doneOcc, @done, 1, 'initial', @at), (@ready_occ, @ready, 1, 'initial', @at), (@claimed_occ, @claimed, 1, 'initial', @at);
			UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @ready_occ WHERE id = @ready;
			UPDATE socalytics.analysis_logical_jobs SET state = 'Ready', current_readiness_occurrence_id = @claimed_occ WHERE id = @claimed;
			UPDATE socalytics.analysis_logical_jobs SET current_readiness_occurrence_id = @doneOcc WHERE id = @done;
			INSERT INTO socalytics.analysis_execution_attempts
				(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at, closed_at)
			VALUES (@doneAttempt, @done, @doneOcc, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Accepted', now(), now(), now(), now());
			INSERT INTO socalytics.analysis_accepted_results
				(id, logical_job_id, run_id, node_id, attempt_id, manifest_reference, manifest_checksum, capability_id, capability_version, schema_version,
				 analyst_profile_digest, image_digest, policy_versions, executed_runtime, accepted_at)
			VALUES (gen_random_uuid(), @done, @run, @n0, @doneAttempt, 'manifest/done', @d, 'cap-a', '1.0.0', '1.0.0', @d, @d, '{}'::jsonb, '{}'::jsonb, now());
			UPDATE socalytics.analysis_logical_jobs SET state = 'Completed' WHERE id = @done;
			UPDATE socalytics.analysis_workflow_nodes SET state = 'Completed' WHERE id = @n0;
			INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, run_id, logical_job_id, capability_id, state, failure_category, next_attempt_at, created_at)
			VALUES (@m1, 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{}'::jsonb, gen_random_uuid(), @run, @ready, 'cap-a', 'pending', NULL, NULL, @at),
			       (@m2, 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{}'::jsonb, gen_random_uuid(), @run, @claimed, 'cap-a', 'failed', 'publish-timeout', @at, @at);
			""",
			new
			{
				run = s.Run, team = hierarchy.TeamId, match = hierarchy.MatchId, set = recordingSet, wf = resolved.Definition.WorkflowId,
				wfv = resolved.Definition.WorkflowVersion, at, n0 = s.DoneNode, n1 = s.ActiveNode, n2 = s.WaitingNode, n3 = s.ClaimedNode, done = s.DoneJob,
				ready = s.ReadyJob, claimed = s.ClaimedJob, waiting = s.WaitingJob, d = Digest, doneOcc = Guid.NewGuid(),
				ready_occ = s.ReadyOccurrence, claimed_occ = s.ClaimedOccurrence, doneAttempt = Guid.NewGuid(),
				m1 = s.ReadyWorkMessages[0], m2 = s.ReadyWorkMessages[1],
			},
			cancellationToken: ct));
		return s;
	}

	private static JsonObject CompletionBody(Seeded s, ClubHierarchy h, Guid attempt, long token) => new()
	{
		["contractVersion"] = "1.0.0",
		["idempotencyKey"] = "late-1",
		["teamId"] = h.TeamId,
		["matchId"] = h.MatchId,
		["runId"] = s.Run,
		["nodeId"] = s.ClaimedNode,
		["logicalJobId"] = s.ClaimedJob,
		["attemptId"] = attempt,
		["attemptNumber"] = 1,
		["fencingToken"] = token,
		["outcome"] = "failed",
		["capability"] = new JsonObject { ["id"] = "cap-a", ["version"] = "1.0.0" },
		["schemaVersion"] = "1.0.0",
		["executedRuntime"] = new JsonObject
		{
			["runtimeProfile"] = "cpu", ["runtimeVersion"] = "1.0", ["operatingSystem"] = "linux",
			["architecture"] = "x64", ["accelerators"] = new JsonArray(),
		},
		["executedImageDigest"] = Digest,
		["executedModelDigest"] = Digest,
		["acceptedUpstreamResultIds"] = new JsonArray(),
		["completedAt"] = "2026-01-01T00:00:00Z",
		["correlation"] = new JsonObject { ["correlationId"] = Guid.NewGuid() },
		["failure"] = new JsonObject { ["category"] = "analyst-error" },
	};

	private static async Task AssertObsoleteAsync(HttpResponseMessage response, string code, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, text);
		text.ShouldContain(code);
	}

	[Fact]
	public async Task CancelledRunFencesAllLaterWork()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var set = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var s = await SeedRunAsync(host, db, hierarchy, set.RecordingSetVersionId, ct);
		var (otherJob, otherOccurrence) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
		var otherRun = Guid.Parse(await CountTextAsync(db, $"SELECT run_id::text FROM socalytics.analysis_logical_jobs WHERE id = '{otherJob}'", ct));
		await using (var c = await OpenAsync(db, ct))
		{
			await c.ExecuteAsync(new CommandDefinition(
				"UPDATE socalytics.analysis_runs SET team_id = @t, match_id = @m WHERE id = @r",
				new { t = hierarchy.TeamId, m = hierarchy.MatchId, r = otherRun }, cancellationToken: ct));
		}

		using var manager = new TestManagerClient(host.Factory);
		using var claim = await manager.ClaimAsync(s.ClaimedJob, s.ClaimedOccurrence, "claim-1", ct: ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
		var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
		var attempt = claimed.GetProperty("attemptId").GetGuid();
		var token = claimed.GetProperty("fencingToken").GetInt64();

		var acceptedBefore = await SnapshotRowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{s.Run}'", ct);
		var otherBefore = await SnapshotAsync(db, otherRun, ct);
		var budgetBefore = await SnapshotRowsAsync(db,
			$"SELECT id, attempts_used FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}'", ct);

		var version = await CountAsync(db, $"SELECT version FROM socalytics.analysis_runs WHERE id = '{s.Run}'", ct);
		var cancel = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/runs/{s.Run}/cancellation");
		cancel.Headers.Add("X-CSRF-Token", coach.AntiforgeryToken);
		cancel.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
		cancel.Content = JsonContent.Create(new { reason = "Wrong recordings." });
		using (var cancelled = await coach.Client.SendAsync(cancel, ct))
		{
			cancelled.StatusCode.ShouldBe(HttpStatusCode.OK, await cancelled.Content.ReadAsStringAsync(ct));
		}

		(await CountAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE run_id = '{s.Run}' AND message_type = 'analysis.ready-work' AND state = 'discarded'", ct)).ShouldBe(2);
		(await CountAsync(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}' AND state = 'Cancelled'", ct)).ShouldBe(3);
		(await SnapshotRowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{s.Run}'", ct)).ShouldBe(acceptedBefore);
		(await SnapshotRowsAsync(db, $"SELECT id, attempts_used FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}'", ct)).ShouldBe(budgetBefore);

		using (var read = await coach.Client.GetAsync($"/api/v1/analysis/runs/{s.Run}", ct))
		{
			var body = await read.Content.ReadFromJsonAsync<JsonElement>(ct);
			body.GetProperty("state").GetString().ShouldBe("Cancelled");
			var nodes = body.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("nodeKey").GetString()!);
			nodes["m0"].GetProperty("state").GetString().ShouldBe("Completed");
			nodes["m0"].GetProperty("jobs").GetProperty("completed").GetInt32().ShouldBe(1);
			nodes["m1"].GetProperty("state").GetString().ShouldBe("Cancelled");
			nodes["m1"].GetProperty("jobs").GetProperty("cancelled").GetInt32().ShouldBe(1);
			nodes["m3"].GetProperty("state").GetString().ShouldBe("Cancelled");
			nodes["m3"].GetProperty("jobs").GetProperty("cancelled").GetInt32().ShouldBe(1);
			nodes["m2"].GetProperty("state").GetString().ShouldBe("Cancelled");
			nodes["m2"].GetProperty("jobs").GetProperty("cancelled").GetInt32().ShouldBe(1);
		}

		var afterCancel = await SnapshotAsync(db, s.Run, ct);

		// Queued messages and claims.
		var attemptCount = await CountAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct);
		using (var readyClaim = await manager.ClaimAsync(s.ReadyJob, s.ReadyOccurrence, "claim-2", ct: ct))
		{
			await AssertObsoleteAsync(readyClaim, "claim-obsolete", ct);
		}

		using (var claimedClaim = await manager.ClaimAsync(s.ClaimedJob, s.ClaimedOccurrence, "claim-3", ct: ct))
		{
			await AssertObsoleteAsync(claimedClaim, "claim-obsolete", ct);
		}

		using (var waitingClaim = await manager.ClaimAsync(s.WaitingJob, Guid.NewGuid(), "claim-4", ct: ct))
		{
			await AssertObsoleteAsync(waitingClaim, "claim-obsolete", ct);
		}

		(await CountAsync(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(attemptCount);

		// Calls of the closed attempt.
		using (var heartbeat = await manager.HeartbeatAsync(attempt, s.ClaimedJob, token, ct))
		{
			await AssertObsoleteAsync(heartbeat, "attempt-obsolete", ct);
		}

		using (var grants = await manager.RequestResultGrantsAsync(attempt,
			new { fencingToken = token, objects = new[] { new { name = "facts.json", sizeBytes = 10, digest = Digest } } }, ct))
		{
			await AssertObsoleteAsync(grants, "attempt-obsolete", ct);
		}

		using (var upstream = await manager.ListUpstreamResultsAsync(attempt,
			$"fencingToken={token}&capabilityId=cap-a&capabilityVersion=1.0.0&schemaVersion=1.0.0", ct))
		{
			await AssertObsoleteAsync(upstream, "attempt-obsolete", ct);
		}

		using (var client = host.Factory.CreateApiClient())
		{
			using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion")
			{
				Content = JsonContent.Create(CompletionBody(s, hierarchy, attempt, token)),
			};
			request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, manager.ManagerId.ToString());
			using var completion = await client.SendAsync(request, ct);
			await AssertObsoleteAsync(completion, "attempt-obsolete", ct);
		}

		// Workers change nothing.
		host.Time.Advance(TimeSpan.FromMinutes(10));
		await using (var workers = host.Services.CreateAsyncScope())
		{
			(await workers.ServiceProvider.GetRequiredService<ExpireStaleAttemptsHandler>().HandleAsync(ct)).ShouldBe(0);
		}

		await using (var readiness = host.Services.CreateAsyncScope())
		{
			await using var uow = await readiness.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await readiness.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(s.Run, ct);
			var applied = await readiness.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(s.Run, ct);
			applied.ReadyJobs.ShouldBe(0);
			applied.RunTransitions.ShouldBe(0);
			await uow.CommitAsync(ct);
		}

		await using (var reconcile = host.Services.CreateAsyncScope())
		{
			var result = await reconcile.ServiceProvider.GetRequiredService<ReconcileAnalysisPublicationHandler>().HandleAsync(ct);
			result.ReadyJobsRepublished.ShouldBe(0);
		}

		// A redelivered notification reports the existing run.
		await using (var notify = host.Services.CreateAsyncScope())
		{
			var lineage = (await notify.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(set.RecordingSetVersionId, ct))!;
			var notification = new RecordingsFinalizedNotification(
				Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
				lineage.Members.Select(m => new FinalizedNotificationMember(m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList());
			var outcome = await notify.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
			outcome.ShouldBeOfType<NotificationOutcome.RunExists>().RunId.ShouldBe(s.Run);
		}

		await using (var publish = host.Services.CreateAsyncScope())
		{
			await publish.ServiceProvider.GetRequiredService<PublishOutboxBatch>().HandleAsync(ct);
		}

		var published = await host.ReadStreamAsync("SOCALYTICS_ANALYSIS_JOBS", ct);
		published.ShouldNotContain(m => m.MessageId != null && s.ReadyWorkMessages.Any(id => id.ToString() == m.MessageId));
		(await CountAsync(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE run_id = '{s.Run}' AND message_type = 'analysis.ready-work'", ct)).ShouldBe(2);

		var afterWorkers = await SnapshotAsync(db, s.Run, ct);
		NormalizePublication(afterWorkers).ShouldBe(NormalizePublication(afterCancel));

		// Other runs of the same match are unaffected.
		(await SnapshotAsync(db, otherRun, ct)).ShouldBe(otherBefore);
		using var otherClaim = await manager.ClaimAsync(otherJob, otherOccurrence, "other-1", ct: ct);
		otherClaim.StatusCode.ShouldBe(HttpStatusCode.Created, await otherClaim.Content.ReadAsStringAsync(ct));
	}

	// Publishing the run-state-changed notification legitimately marks its outbox row as published.
	private static string NormalizePublication(string snapshot)
	{
		var parts = snapshot.Split("\n--\n");
		var outbox = parts[^1].Split('\n').Where(l => l.Contains("analysis.ready-work", StringComparison.Ordinal));
		return string.Join("\n--\n", parts[..^1]) + "\n--\n" + string.Join("\n", outbox);
	}

	private static async Task<string> SnapshotRowsAsync(IsolatedDatabase db, string query, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		var rows = await c.QueryAsync<string>(new CommandDefinition(
			$"SELECT row_to_json(t)::text FROM ({query} ORDER BY 1) t", cancellationToken: ct));
		return string.Join("\n", rows);
	}

	private static async Task<string> CountTextAsync(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		return (await c.ExecuteScalarAsync<string>(new CommandDefinition(sql, cancellationToken: ct)))!;
	}
}

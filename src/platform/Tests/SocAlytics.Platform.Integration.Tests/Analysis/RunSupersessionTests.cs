using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class RunSupersessionTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
	private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

	private sealed record Seeded(
		Guid Run, Guid ReadyJob, Guid ReadyOccurrence, Guid ClaimedNode, Guid ClaimedJob, Guid ClaimedOccurrence, Guid WaitingJob, Guid AcceptedResult);

	private sealed class SwitchResolver(IWorkflowDefinitionResolver inner, SwitchResolver.Flag flag) : IWorkflowDefinitionResolver
	{
		public sealed class Flag { public volatile bool Invalid; }

		public async Task<WorkflowResolution> ResolveDefaultAsync(CancellationToken ct)
		{
			var resolution = await inner.ResolveDefaultAsync(ct);
			return flag.Invalid && resolution is WorkflowResolution.ResolvedResult resolved
				? WorkflowResolution.Resolved(resolved.Definition with { RequestedCapabilities = [] })
				: resolution;
		}
	}

	private static async Task<NpgsqlConnection> OpenAsync(IsolatedDatabase db, CancellationToken ct)
	{
		var c = new NpgsqlConnection(db.MigratorConnectionString);
		await c.OpenAsync(ct);
		return c;
	}

	private static async Task<T> ScalarAsync<T>(IsolatedDatabase db, string sql, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		return (await c.ExecuteScalarAsync<T>(new CommandDefinition(sql, cancellationToken: ct)))!;
	}

	private static async Task<string> RowsAsync(IsolatedDatabase db, string query, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		var rows = await c.QueryAsync<string>(new CommandDefinition($"SELECT row_to_json(t)::text FROM ({query} ORDER BY 1) t", cancellationToken: ct));
		return string.Join("\n", rows);
	}

	private static Task<string> RunSnapshotAsync(IsolatedDatabase db, Guid run, CancellationToken ct) => RowsAsync(db,
		$"""
		SELECT 'run' AS k, to_jsonb(r)::text AS v FROM socalytics.analysis_runs r WHERE id = '{run}'
		UNION ALL SELECT 'node', to_jsonb(n)::text FROM socalytics.analysis_workflow_nodes n WHERE run_id = '{run}'
		UNION ALL SELECT 'job', to_jsonb(j)::text FROM socalytics.analysis_logical_jobs j WHERE run_id = '{run}'
		UNION ALL SELECT 'outbox', to_jsonb(o)::text FROM socalytics.outbox_messages o WHERE run_id = '{run}'
		""", ct);

	private async Task<(AnalysisTestHost Host, IsolatedDatabase Db, ApiSession Coach, ClubHierarchy Hierarchy, SwitchResolver.Flag Flag)> StartAsync(CancellationToken ct)
	{
		var flag = new SwitchResolver.Flag();
		var db = await postgres.CreateDatabaseAsync(ct);
		(await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
		var host = new AnalysisTestHost(db, nats, rustFs, time: new FakeTimeProvider(DateTimeOffset.UtcNow), configureServices: services =>
		{
			var original = services.Last(d => d.ServiceType == typeof(IWorkflowDefinitionResolver));
			services.Remove(original);
			services.AddSingleton<IWorkflowDefinitionResolver>(sp => new SwitchResolver(
				(IWorkflowDefinitionResolver)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), flag));
		});
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
		return (host, db, coach, hierarchy, flag);
	}

	private static async Task<RecordingsFinalizedNotification> NotificationAsync(AnalysisTestHost host, Guid setVersion, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(setVersion, ct))!;
		return new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			lineage.Members.Select(m => new FinalizedNotificationMember(m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList());
	}

	private static async Task<DateTimeOffset> FinalizedAtAsync(AnalysisTestHost host, Guid setVersion, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		return (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(setVersion, ct))!.FinalizedAt;
	}

	private static async Task<Guid> HandleAsync(AnalysisTestHost host, RecordingsFinalizedNotification notification, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		var outcome = await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
		return outcome.ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
	}

	private static async Task<Seeded> SeedRunningAsync(
		AnalysisTestHost host, IsolatedDatabase db, ClubHierarchy hierarchy, Guid recordingSet, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		var resolved = (WorkflowResolution.ResolvedResult)await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct);
		var finalizedAt = await FinalizedAtAsync(host, recordingSet, ct);
		var s = new Seeded(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
		var doneNode = Guid.NewGuid();
		var readyNode = Guid.NewGuid();
		var waitingNode = Guid.NewGuid();
		var doneJob = Guid.NewGuid();
		var at = DateTimeOffset.UtcNow;
		await using var c = await OpenAsync(db, ct);
		await c.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, updated_at)
			VALUES (@run, @team, @match, @set, @fin, @wf, @wfv, ARRAY['cap-a'], 'all-requested-capabilities', 'Running', @at, @at, @at);
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
			VALUES (@result, @done, @run, @n0, @doneAttempt, 'manifest/done', @d, 'cap-a', '1.0.0', '1.0.0', @d, @d, '{}'::jsonb, '{}'::jsonb, now());
			UPDATE socalytics.analysis_logical_jobs SET state = 'Completed' WHERE id = @done;
			UPDATE socalytics.analysis_workflow_nodes SET state = 'Completed' WHERE id = @n0;
			INSERT INTO socalytics.outbox_messages (id, message_type, contract_version, subject, payload, causation_id, run_id, logical_job_id, capability_id, state, failure_category, next_attempt_at, created_at)
			VALUES (gen_random_uuid(), 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{}'::jsonb, gen_random_uuid(), @run, @ready, 'cap-a', 'pending', NULL, NULL, @at),
			       (gen_random_uuid(), 'analysis.ready-work', '1.0.0', 'analysis.jobs.ready.cap-a', '{}'::jsonb, gen_random_uuid(), @run, @claimed, 'cap-a', 'failed', 'publish-timeout', @at, @at);
			""",
			new
			{
				run = s.Run, team = hierarchy.TeamId, match = hierarchy.MatchId, set = recordingSet, fin = finalizedAt, wf = resolved.Definition.WorkflowId,
				wfv = resolved.Definition.WorkflowVersion, at, n0 = doneNode, n1 = readyNode, n2 = waitingNode, n3 = s.ClaimedNode, done = doneJob,
				ready = s.ReadyJob, claimed = s.ClaimedJob, waiting = s.WaitingJob, d = Digest, doneOcc = Guid.NewGuid(),
				ready_occ = s.ReadyOccurrence, claimed_occ = s.ClaimedOccurrence, doneAttempt = Guid.NewGuid(), result = s.AcceptedResult,
			},
			cancellationToken: ct));
		return s;
	}

	private static async Task<Guid> SeedRequestedAsync(
		AnalysisTestHost host, IsolatedDatabase db, ClubHierarchy hierarchy, Guid recordingSet, CancellationToken ct)
	{
		var run = Guid.NewGuid();
		var node = Guid.NewGuid();
		var at = DateTimeOffset.UtcNow;
		var finalizedAt = await FinalizedAtAsync(host, recordingSet, ct);
		await using var c = await OpenAsync(db, ct);
		await c.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, updated_at)
			VALUES (@run, @team, @match, @set, @fin, 'wf', '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', 'Requested', @at, @at);
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Waiting', @at);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (gen_random_uuid(), @run, @node, 'match', 'Waiting', @at);
			""",
			new { run, node, team = hierarchy.TeamId, match = hierarchy.MatchId, set = recordingSet, fin = finalizedAt, at }, cancellationToken: ct));
		return run;
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

	private static async Task AssertSupersededAsync(
		AnalysisTestHost host, IsolatedDatabase db, Guid older, Guid newer, string previous, string previousMetric,
		List<(string Name, Dictionary<string, object?> Tags)> sink, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		var row = await c.QuerySingleAsync<(string State, string? Cause, Guid? By, string? Reason, DateTimeOffset? Terminated)>(new CommandDefinition(
			"SELECT state, cancellation_cause, cancelled_by_account_id, cancellation_reason, terminated_at FROM socalytics.analysis_runs WHERE id = @older",
			new { older }, cancellationToken: ct));
		row.State.ShouldBe("Cancelled");
		row.Cause.ShouldBe("superseded");
		row.By.ShouldBeNull();
		row.Reason.ShouldBeNull();

		var supersession = await c.QuerySingleAsync<(Guid By, DateTimeOffset At, string Previous)>(new CommandDefinition(
			"SELECT superseding_run_id, superseded_at, previous_state FROM socalytics.analysis_run_supersessions WHERE run_id = @older",
			new { older }, cancellationToken: ct));
		supersession.By.ShouldBe(newer);
		supersession.Previous.ShouldBe(previous, StringCompareShould.IgnoreCase);
		row.Terminated.ShouldBe(supersession.At);

		var audit = (await c.QueryAsync<(string Kind, string Outcome, string Details)>(new CommandDefinition(
			"SELECT actor_kind, outcome, details::text FROM socalytics.security_audit_event WHERE event_type = 'analysis.run-superseded' AND resource_id = @id",
			new { id = older.ToString() }, cancellationToken: ct))).ToList();
		audit.Count.ShouldBe(1);
		audit[0].Kind.ShouldBe("system");
		audit[0].Outcome.ShouldBe("succeeded");
		var details = JsonDocument.Parse(audit[0].Details).RootElement;
		details.GetProperty("supersedingRunId").GetString().ShouldBe(newer.ToString());
		details.GetProperty("previousState").GetString().ShouldBe(previous);

		var payloads = (await c.QueryAsync<string>(new CommandDefinition(
			"SELECT payload::text FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = @older",
			new { older }, cancellationToken: ct))).ToList();
		payloads.Count.ShouldBe(1);
		var payload = JsonDocument.Parse(payloads[0]).RootElement;
		payload.GetProperty("state").GetString().ShouldBe("Cancelled");
		payload.GetProperty("previousState").GetString().ShouldBe(previous);
		var validation = host.Services.GetRequiredService<IContractValidator>().Validate(ContractSchemaIds.RunStateChangedV1, payload);
		validation.IsValid.ShouldBeTrue(string.Join(", ", validation.Errors.Select(e => e.Field + ":" + e.Keyword)));

		(await c.ExecuteScalarAsync<long>(new CommandDefinition(
			"SELECT count(*) FROM socalytics.analysis_run_supersessions WHERE superseding_run_id = @newer", new { newer }, cancellationToken: ct))).ShouldBe(1);
		(await c.ExecuteScalarAsync<string>(new CommandDefinition(
			"SELECT state FROM socalytics.analysis_runs WHERE id = @newer", new { newer }, cancellationToken: ct))).ShouldBeOneOf("Requested", "Running");
		(await c.ExecuteScalarAsync<long>(new CommandDefinition(
			"SELECT count(*) FROM socalytics.analysis_runs WHERE id = @newer AND cancellation_cause IS NULL", new { newer }, cancellationToken: ct))).ShouldBe(1);

		lock (sink)
		{
			sink.ShouldContain(m => m.Name == "socalytics.analysis.runs_superseded" && (string?)m.Tags["previous"] == previousMetric);
		}
	}

	private static MeterListener Listen(List<(string Name, Dictionary<string, object?> Tags)> sink)
	{
		var listener = new MeterListener
		{
			InstrumentPublished = (i, l) =>
			{
				if (i.Meter.Name == AnalysisMetrics.MeterName)
				{
					l.EnableMeasurementEvents(i);
				}
			},
		};
		listener.SetMeasurementEventCallback<long>((i, _, t, _) => { lock (sink) { sink.Add((i.Name, t.ToArray().ToDictionary(x => x.Key, x => x.Value))); } });
		listener.Start();
		return listener;
	}

	[Fact]
	public async Task NewerVersionSupersedesARunningRunAndFencesItsWork()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy, _) = await StartAsync(ct);
		await using var _ = host;
		var sink = new List<(string Name, Dictionary<string, object?> Tags)>();
		using var listener = Listen(sink);

		var setA = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var s = await SeedRunningAsync(host, db, hierarchy, setA.RecordingSetVersionId, ct);
		var (otherJob, otherOccurrence) = await ExecutionAttemptTests.SeedJobAsync(db, "Ready", ct);
		var otherRun = await ScalarAsync<Guid>(db, $"SELECT run_id FROM socalytics.analysis_logical_jobs WHERE id = '{otherJob}'", ct);
		await using (var c = await OpenAsync(db, ct))
		{
			await c.ExecuteAsync(new CommandDefinition(
				"UPDATE socalytics.analysis_runs SET team_id = @t WHERE id = @r", new { t = hierarchy.TeamId, r = otherRun }, cancellationToken: ct));
		}

		using var manager = new TestManagerClient(host.Factory);
		using var claim = await manager.ClaimAsync(s.ClaimedJob, s.ClaimedOccurrence, "claim-1", ct: ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
		var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
		var attempt = claimed.GetProperty("attemptId").GetGuid();
		var token = claimed.GetProperty("fencingToken").GetInt64();

		var acceptedBefore = await RowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{s.Run}'", ct);
		var budgetBefore = await RowsAsync(db, $"SELECT id, attempts_used FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}'", ct);
		var otherBefore = await RunSnapshotAsync(db, otherRun, ct);
		var attemptCount = await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct);
		host.Time.Advance(TimeSpan.FromSeconds(5));

		var setB = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var newer = await HandleAsync(host, await NotificationAsync(host, setB.RecordingSetVersionId, ct), ct);

		await AssertSupersededAsync(host, db, s.Run, newer, "Running", "active", sink, ct);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE run_id = '{s.Run}' AND message_type = 'analysis.ready-work' AND state = 'discarded'", ct)).ShouldBe(2);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}' AND state = 'Cancelled'", ct)).ShouldBe(3);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE id = '{attempt}' AND closed_at IS NOT NULL", ct)).ShouldBe(1);
		(await RowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{s.Run}'", ct)).ShouldBe(acceptedBefore);
		(await RowsAsync(db, $"SELECT id, attempts_used FROM socalytics.analysis_logical_jobs WHERE run_id = '{s.Run}'", ct)).ShouldBe(budgetBefore);

		// The older run's results are not visible to the newer run.
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = '{newer}'", ct)).ShouldBe(0);
		using (var streams = await coach.Client.GetAsync($"/api/v1/analysis/runs/{newer}/fact-streams", ct))
		{
			var text = await streams.Content.ReadAsStringAsync(ct);
			streams.StatusCode.ShouldBe(HttpStatusCode.OK, text);
			text.ShouldNotContain(s.Run.ToString());
			text.ShouldNotContain(s.AcceptedResult.ToString());
		}

		// Queued claims and calls of the active attempt are obsolete.
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

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.analysis_execution_attempts", ct)).ShouldBe(attemptCount);

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

		(await RowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{s.Run}'", ct)).ShouldBe(acceptedBefore);

		// A run of another match of the same team is unchanged and still claimable.
		(await RunSnapshotAsync(db, otherRun, ct)).ShouldBe(otherBefore);
		using var otherClaim = await manager.ClaimAsync(otherJob, otherOccurrence, "other-1", ct: ct);
		otherClaim.StatusCode.ShouldBe(HttpStatusCode.Created, await otherClaim.Content.ReadAsStringAsync(ct));
	}

	[Fact]
	public async Task NewerVersionSupersedesARequestedRun()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy, _) = await StartAsync(ct);
		await using var _ = host;
		var sink = new List<(string Name, Dictionary<string, object?> Tags)>();
		using var listener = Listen(sink);

		var setA = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var older = await SeedRequestedAsync(host, db, hierarchy, setA.RecordingSetVersionId, ct);
		host.Time.Advance(TimeSpan.FromSeconds(5));

		var setB = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var newer = await HandleAsync(host, await NotificationAsync(host, setB.RecordingSetVersionId, ct), ct);

		await AssertSupersededAsync(host, db, older, newer, "Requested", "active", sink, ct);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_logical_jobs WHERE run_id = '{older}' AND state = 'Cancelled'", ct)).ShouldBe(1);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE run_id = '{older}' AND message_type = 'analysis.ready-work'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task NewerRunCreatedAsFailedStillSupersedes()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy, flag) = await StartAsync(ct);
		await using var _ = host;

		var setA = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var older = await SeedRequestedAsync(host, db, hierarchy, setA.RecordingSetVersionId, ct);
		host.Time.Advance(TimeSpan.FromSeconds(5));

		var setB = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		flag.Invalid = true;
		var newer = await HandleAsync(host, await NotificationAsync(host, setB.RecordingSetVersionId, ct), ct);

		(await ScalarAsync<string>(db, $"SELECT state FROM socalytics.analysis_runs WHERE id = '{newer}'", ct)).ShouldBe("Failed");
		(await ScalarAsync<string>(db, $"SELECT state FROM socalytics.analysis_runs WHERE id = '{older}'", ct)).ShouldBe("Cancelled");
		(await ScalarAsync<string>(db, $"SELECT cancellation_cause FROM socalytics.analysis_runs WHERE id = '{older}'", ct)).ShouldBe("superseded");
		(await ScalarAsync<Guid>(db, $"SELECT superseding_run_id FROM socalytics.analysis_run_supersessions WHERE run_id = '{older}'", ct)).ShouldBe(newer);
	}

	private static async Task<Guid> SeedEndedAsync(
		IsolatedDatabase db, ClubHierarchy hierarchy, Guid recordingSet, DateTimeOffset finalizedAt, string state, string workflow, CancellationToken ct)
	{
		var run = Guid.NewGuid();
		var node = Guid.NewGuid();
		var at = DateTimeOffset.UtcNow.AddMinutes(-1);
		var terminal = state is "PartiallyCompleted" or "Failed";
		await using var c = await OpenAsync(db, ct);
		await c.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, failure_reason, created_at, running_at, terminated_at, updated_at)
			VALUES (@run, @team, @match, @set, @fin, @wf, '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', @state, @failure, @at, @running, @terminated, @at);
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (gen_random_uuid(), @run, @node, 'match', 'Waiting', @at);
			""",
			new
			{
				run, node, team = hierarchy.TeamId, match = hierarchy.MatchId, set = recordingSet, fin = finalizedAt, wf = workflow, state, at,
				failure = state == "Failed" ? "capability-failed" : null,
				running = state == "Requested" ? (DateTimeOffset?)null : at,
				terminated = terminal ? at : (DateTimeOffset?)null,
			},
			cancellationToken: ct));
		return run;
	}

	private static async Task<HttpResponseMessage> CancelAsync(ApiSession session, Guid run, long version, CancellationToken ct)
	{
		var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/runs/{run}/cancellation");
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.TryAddWithoutValidation("If-Match", $"\"{version}\"");
		request.Content = JsonContent.Create(new { reason = "Wrong recordings." });
		return await session.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> GetJsonAsync(ApiSession session, string url, CancellationToken ct)
	{
		using var response = await session.Client.GetAsync(url, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
		return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
	}

	private static async Task<(JsonElement Status, JsonElement Item)> ReadRunAsync(ApiSession session, ClubHierarchy hierarchy, Guid run, CancellationToken ct)
	{
		var status = await GetJsonAsync(session, $"/api/v1/analysis/runs/{run}", ct);
		var list = await GetJsonAsync(session, $"/api/v1/matches/{hierarchy.MatchId}/analysis-runs", ct);
		var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("runId").GetGuid() == run);
		return (status, item);
	}

	private static bool IsNull(JsonElement e, string name) => !e.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null;

	[Fact]
	public async Task EndedRunsKeepTheirRecordsAndReportSupersessionThroughReads()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy, _) = await StartAsync(ct);
		await using var _ = host;
		var sink = new List<(string Name, Dictionary<string, object?> Tags)>();
		using var listener = Listen(sink);

		(await GetJsonAsync(coach, $"/api/v1/matches/{hierarchy.MatchId}/analysis-runs", ct)).GetProperty("items").GetArrayLength().ShouldBe(0);

		var setA = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var finalizedAt = await FinalizedAtAsync(host, setA.RecordingSetVersionId, ct);
		var seeded = await SeedRunningAsync(host, db, hierarchy, setA.RecordingSetVersionId, ct);
		var completed = seeded.Run;
		await using (var c = await OpenAsync(db, ct))
		{
			await c.ExecuteAsync(new CommandDefinition(
				"UPDATE socalytics.analysis_runs SET state = 'Completed', terminated_at = now() WHERE id = @completed", new { completed }, cancellationToken: ct));
		}

		var partial = await SeedEndedAsync(db, hierarchy, setA.RecordingSetVersionId, finalizedAt, "PartiallyCompleted", "wf-partial", ct);
		var failed = await SeedEndedAsync(db, hierarchy, setA.RecordingSetVersionId, finalizedAt, "Failed", "wf-failed", ct);
		var cancelled = await SeedEndedAsync(db, hierarchy, setA.RecordingSetVersionId, finalizedAt, "Running", "wf-cancelled", ct);
		var cancelVersion = await ScalarAsync<long>(db, $"SELECT version FROM socalytics.analysis_runs WHERE id = '{cancelled}'", ct);
		using (var cancel = await CancelAsync(coach, cancelled, cancelVersion, ct))
		{
			cancel.StatusCode.ShouldBe(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync(ct));
		}

		var ended = new[] { completed, partial, failed, cancelled };
		var states = new Dictionary<Guid, string> { [completed] = "Completed", [partial] = "PartiallyCompleted", [failed] = "Failed", [cancelled] = "Cancelled" };
		var before = new Dictionary<Guid, string>();
		var outboxBefore = new Dictionary<Guid, long>();
		var statusBefore = new Dictionary<Guid, JsonElement>();
		foreach (var run in ended)
		{
			before[run] = await RunSnapshotAsync(db, run, ct);
			outboxBefore[run] = await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = '{run}'", ct);
			statusBefore[run] = (await ReadRunAsync(coach, hierarchy, run, ct)).Status;
			(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_run_supersessions WHERE run_id = '{run}'", ct)).ShouldBe(0);
		}

		var acceptedBefore = await RowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{completed}'", ct);
		var auditBefore = await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run-superseded'", ct);
		var cancelledAuditBefore = await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE resource_id = '{cancelled}'", ct);
		host.Time.Advance(TimeSpan.FromSeconds(5));

		var setB = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var newer = await HandleAsync(host, await NotificationAsync(host, setB.RecordingSetVersionId, ct), ct);

		foreach (var run in ended)
		{
			var snapshotAfter = await RunSnapshotAsync(db, run, ct);
			snapshotAfter.ShouldBe(before[run]);

			await using var c = await OpenAsync(db, ct);
			var supersession = await c.QuerySingleAsync<(Guid By, DateTimeOffset At, string Previous)>(new CommandDefinition(
				"SELECT superseding_run_id, superseded_at, previous_state FROM socalytics.analysis_run_supersessions WHERE run_id = @run",
				new { run }, cancellationToken: ct));
			supersession.By.ShouldBe(newer);
			supersession.Previous.ShouldBe(states[run], StringCompareShould.IgnoreCase);
			(await c.ExecuteScalarAsync<long>(new CommandDefinition(
				"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND run_id = @run",
				new { run }, cancellationToken: ct))).ShouldBe(outboxBefore[run]);

			var (status, item) = await ReadRunAsync(coach, hierarchy, run, ct);
			foreach (var view in new[] { status, item })
			{
				view.GetProperty("state").GetString().ShouldBe(states[run]);
				view.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
				view.GetProperty("supersededBy").GetProperty("runId").GetGuid().ShouldBe(newer);
				view.GetProperty("supersededBy").GetProperty("recordingSetVersionId").GetGuid().ShouldBe(setB.RecordingSetVersionId);
				view.GetProperty("supersededBy").GetProperty("recordingSetFinalizedAt").ValueKind.ShouldBe(JsonValueKind.String);
				view.GetProperty("supersededAt").GetDateTimeOffset().ShouldBe(supersession.At);
				view.GetProperty("terminatedAt").ValueKind.ShouldBe(JsonValueKind.String);
				view.GetProperty("version").GetInt64().ShouldBe(statusBefore[run].GetProperty("version").GetInt64());
				view.GetProperty("terminatedAt").GetDateTimeOffset().ShouldBe(statusBefore[run].GetProperty("terminatedAt").GetDateTimeOffset());
				if (run == failed)
				{
					view.GetProperty("failureReason").GetString().ShouldBe("capability-failed");
				}

				if (run == cancelled)
				{
					view.GetProperty("cancellationCause").GetString().ShouldBe("member");
					view.GetProperty("cancelledBy").GetProperty("accountName").GetString().ShouldBe("coach-1");
					view.GetProperty("cancellationReason").GetString().ShouldBe("Wrong recordings.");
				}
				else
				{
					IsNull(view, "cancellationCause").ShouldBeTrue();
					IsNull(view, "cancelledBy").ShouldBeTrue();
				}
			}
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run-superseded'", ct)).ShouldBe(auditBefore);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE resource_id = '{cancelled}'", ct)).ShouldBe(cancelledAuditBefore);
		(await RowsAsync(db, $"SELECT * FROM socalytics.analysis_accepted_results WHERE run_id = '{completed}'", ct)).ShouldBe(acceptedBefore);
		lock (sink)
		{
			sink.Count(m => m.Name == "socalytics.analysis.runs_superseded" && (string?)m.Tags["previous"] == "ended").ShouldBe(4);
			sink.ShouldNotContain(m => m.Name == "socalytics.analysis.runs_superseded" && (string?)m.Tags["previous"] == "active");
		}

		// Results of superseded runs stay readable through the existing reads.
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.analysis_accepted_results WHERE run_id = '{completed}'", ct)).ShouldBe(1);
		(await GetJsonAsync(coach, $"/api/v1/analysis/runs/{completed}", ct)).GetProperty("state").GetString().ShouldBe("Completed");

		// The cancellation feature's idempotent answer works because the run's version did not change.
		var cancelledVersion = statusBefore[cancelled].GetProperty("version").GetInt64();
		using (var repeat = await CancelAsync(coach, cancelled, cancelledVersion, ct))
		{
			repeat.StatusCode.ShouldBe(HttpStatusCode.OK, await repeat.Content.ReadAsStringAsync(ct));
			var body = await repeat.Content.ReadFromJsonAsync<JsonElement>(ct);
			body.GetProperty("version").GetInt64().ShouldBe(cancelledVersion);
			body.GetProperty("cancellationCause").GetString().ShouldBe("member");
			body.GetProperty("state").GetString().ShouldBe("Cancelled");
		}

		(await ScalarAsync<long>(db, $"SELECT version FROM socalytics.analysis_runs WHERE id = '{cancelled}'", ct)).ShouldBe(cancelledVersion);
		(await ScalarAsync<long>(db, $"SELECT count(*) FROM socalytics.security_audit_event WHERE resource_id = '{cancelled}'", ct)).ShouldBe(cancelledAuditBefore);

		// Exactly one run of the match is current.
		var list = await GetJsonAsync(coach, $"/api/v1/matches/{hierarchy.MatchId}/analysis-runs", ct);
		var current = list.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("isCurrent").GetBoolean()).ToList();
		current.Count.ShouldBe(1);
		current[0].GetProperty("runId").GetGuid().ShouldBe(newer);
		IsNull(current[0], "supersededBy").ShouldBeTrue();
		IsNull(current[0], "supersededAt").ShouldBeTrue();

		// Members without a role on the owning team get not found.
		await TestMembers.SeedAsync(db, "outsider-1", cancellationToken: ct);
		var outsider = await ApiSession.SignInAsync(host.Factory, "outsider-1", TestMembers.DefaultPassword, ct);
		using var hiddenRun = await outsider.Client.GetAsync($"/api/v1/analysis/runs/{completed}", ct);
		hiddenRun.StatusCode.ShouldBe(HttpStatusCode.NotFound);
		using var hiddenList = await outsider.Client.GetAsync($"/api/v1/matches/{hierarchy.MatchId}/analysis-runs", ct);
		hiddenList.StatusCode.ShouldBe(HttpStatusCode.NotFound);
	}

	[Fact]
	public async Task SupersededCancellationReportsCauseWithoutMemberAndCurrentRunMovesWithNewerVersions()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy, _) = await StartAsync(ct);
		await using var _ = host;

		var setA = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var older = await SeedRequestedAsync(host, db, hierarchy, setA.RecordingSetVersionId, ct);
		host.Time.Advance(TimeSpan.FromSeconds(5));

		var setB = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var middle = await HandleAsync(host, await NotificationAsync(host, setB.RecordingSetVersionId, ct), ct);

		var (status, item) = await ReadRunAsync(coach, hierarchy, older, ct);
		foreach (var view in new[] { status, item })
		{
			view.GetProperty("state").GetString().ShouldBe("Cancelled");
			view.GetProperty("cancellationCause").GetString().ShouldBe("superseded");
			IsNull(view, "cancelledBy").ShouldBeTrue();
			IsNull(view, "cancellationReason").ShouldBeTrue();
			view.GetProperty("isCurrent").GetBoolean().ShouldBeFalse();
			view.GetProperty("supersededBy").GetProperty("runId").GetGuid().ShouldBe(middle);
			view.GetProperty("supersededAt").ValueKind.ShouldBe(JsonValueKind.String);
		}

		host.Time.Advance(TimeSpan.FromSeconds(5));
		var setC = await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct);
		var newest = await HandleAsync(host, await NotificationAsync(host, setC.RecordingSetVersionId, ct), ct);

		var list = await GetJsonAsync(coach, $"/api/v1/matches/{hierarchy.MatchId}/analysis-runs", ct);
		var items = list.GetProperty("items").EnumerateArray().ToList();
		items.Count.ShouldBe(3);
		items.Where(i => i.GetProperty("isCurrent").GetBoolean()).Select(i => i.GetProperty("runId").GetGuid()).ShouldBe([newest]);
		items.Single(i => i.GetProperty("runId").GetGuid() == middle).GetProperty("supersededBy").GetProperty("runId").GetGuid().ShouldBe(newest);
		items.Single(i => i.GetProperty("runId").GetGuid() == older).GetProperty("supersededBy").GetProperty("runId").GetGuid().ShouldBe(middle);
	}
}

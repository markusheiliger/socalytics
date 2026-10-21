using System.Net;
using System.Net.Http.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

public sealed class RunSupersessionOrderingTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
	private sealed record Versions(Guid[] Sets, Guid[] Runs);

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

	private static async Task<Guid[]> FinalizeAsync(AnalysisTestHost host, ApiSession coach, ClubHierarchy hierarchy, int count, CancellationToken ct)
	{
		var sets = new List<Guid>();
		for (var i = 0; i < count; i++)
		{
			host.Time.Advance(TimeSpan.FromSeconds(5));
			sets.Add((await RecordingFinalizationHelper.FinalizeAsync(coach, hierarchy.MatchId, 1, ct)).RecordingSetVersionId);
		}

		return [.. sets];
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
		return outcome switch
		{
			NotificationOutcome.RunCreated created => created.RunId,
			NotificationOutcome.RunExists exists => exists.RunId,
			_ => throw new ShouldAssertException($"Unexpected outcome {outcome}."),
		};
	}

	private static async Task DeliverAsync(AnalysisTestHost host, Guid set, bool duplicates, CancellationToken ct)
	{
		var notification = await NotificationAsync(host, set, ct);
		var run = await HandleAsync(host, notification, ct);
		if (duplicates)
		{
			(await HandleAsync(host, notification, ct)).ShouldBe(run);
			(await HandleAsync(host, notification with { EventId = Guid.NewGuid() }, ct)).ShouldBe(run);
		}
	}

	private static Task<Guid> RunOfAsync(IsolatedDatabase db, Guid set, CancellationToken ct) =>
		ScalarAsync<Guid>(db, $"SELECT id FROM socalytics.analysis_runs WHERE recording_set_version_id = '{set}' ORDER BY created_at LIMIT 1", ct);

	private static Task<long> AuditCountAsync(IsolatedDatabase db, string eventType, Guid? run, CancellationToken ct) => ScalarAsync<long>(db,
		$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}'" + (run is null ? "" : $" AND resource_id = '{run}'"), ct);

	private static Task<long> CancelledNotificationsAsync(IsolatedDatabase db, Guid? run, CancellationToken ct) => ScalarAsync<long>(db,
		"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed' AND payload->>'state' = 'Cancelled'"
		+ (run is null ? "" : $" AND run_id = '{run}'"), ct);

	private static Task<long> ReadyWorkAsync(IsolatedDatabase db, Guid run, CancellationToken ct) => ScalarAsync<long>(db,
		$"SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work' AND run_id = '{run}'", ct);

	private static async Task<Dictionary<Guid, (Guid By, string Previous)>> SupersessionsAsync(IsolatedDatabase db, CancellationToken ct)
	{
		await using var c = await OpenAsync(db, ct);
		var rows = await c.QueryAsync<(Guid Run, Guid By, string Previous)>(new CommandDefinition(
			"SELECT run_id, superseding_run_id, previous_state FROM socalytics.analysis_run_supersessions", cancellationToken: ct));
		return rows.ToDictionary(r => r.Run, r => (r.By, r.Previous));
	}

	private static async Task<string> StateAsync(IsolatedDatabase db, Guid run, CancellationToken ct) =>
		await ScalarAsync<string>(db, $"SELECT state || '/' || coalesce(cancellation_cause, '') FROM socalytics.analysis_runs WHERE id = '{run}'", ct);

	private static async Task<ReconciliationResult> ReconcileAsync(AnalysisTestHost host, CancellationToken ct)
	{
		await using var scope = host.Services.CreateAsyncScope();
		return await scope.ServiceProvider.GetRequiredService<ReconcileAnalysisPublicationHandler>().HandleAsync(ct);
	}

	private static async Task<Guid> SeedRunAsync(
		IsolatedDatabase db, ClubHierarchy hierarchy, Guid set, DateTimeOffset finalizedAt, string workflow, string state, CancellationToken ct)
	{
		var run = Guid.NewGuid();
		var node = Guid.NewGuid();
		var at = DateTimeOffset.UtcNow.AddMinutes(-1);
		var ended = state == "Completed";
		await using var c = await OpenAsync(db, ct);
		await c.ExecuteAsync(new CommandDefinition(
			"""
			INSERT INTO socalytics.analysis_runs (id, team_id, match_id, recording_set_version_id, recording_set_finalized_at, workflow_id, workflow_version, requested_capabilities, completion_policy, state, created_at, running_at, terminated_at, updated_at)
			VALUES (@run, @team, @match, @set, @fin, @wf, '1.0.0', ARRAY['cap-a'], 'all-requested-capabilities', @state, @at, @running, @terminated, @at);
			INSERT INTO socalytics.analysis_workflow_nodes (id, run_id, node_key, capability_id, capability_version, tier, execution_scope, ordinal, state, updated_at)
			VALUES (@node, @run, 'm1', 'cap-a', '1.0.0', 'high-level', 'match', 0, 'Active', @at);
			INSERT INTO socalytics.analysis_logical_jobs (id, run_id, node_id, execution_scope, state, updated_at)
			VALUES (gen_random_uuid(), @run, @node, 'match', 'Waiting', @at);
			""",
			new
			{
				run, node, team = hierarchy.TeamId, match = hierarchy.MatchId, set, fin = finalizedAt, wf = workflow, state, at,
				running = state == "Requested" ? (DateTimeOffset?)null : at,
				terminated = ended ? at : (DateTimeOffset?)null,
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

	[Theory]
	[InlineData("ABC")]
	[InlineData("CBA")]
	[InlineData("BAC")]
	public async Task DeliveryOrderAndDuplicatesEndInTheSameSupersession(string order)
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 3, ct);

		foreach (var letter in order)
		{
			await DeliverAsync(host, sets[letter - 'A'], duplicates: true, ct);
		}

		var runs = new Guid[3];
		for (var i = 0; i < 3; i++)
		{
			runs[i] = await RunOfAsync(db, sets[i], ct);
		}

		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(3);
		var supersessions = await SupersessionsAsync(db, ct);
		supersessions.Keys.ToHashSet().ShouldBe([runs[0], runs[1]], ignoreOrder: true);

		// A run that is already superseded keeps its first superseding run when a newer version arrives.
		var expectedForA = order.IndexOf('C') < order.IndexOf('A') ? runs[2] : runs[1];
		supersessions[runs[0]].By.ShouldBe(expectedForA);
		supersessions[runs[1]].By.ShouldBe(runs[2]);
		supersessions[runs[0]].Previous.ShouldBeOneOf("Requested", "Running");

		(await StateAsync(db, runs[0], ct)).ShouldBe("Cancelled/superseded");
		(await StateAsync(db, runs[1], ct)).ShouldBe("Cancelled/superseded");
		(await StateAsync(db, runs[2], ct)).ShouldBeOneOf("Requested/", "Running/");

		foreach (var run in runs[..2])
		{
			(await AuditCountAsync(db, "analysis.run-superseded", run, ct)).ShouldBe(1);
			(await CancelledNotificationsAsync(db, run, ct)).ShouldBe(1);
		}

		(await AuditCountAsync(db, "analysis.run-superseded", null, ct)).ShouldBe(2);
		(await CancelledNotificationsAsync(db, null, ct)).ShouldBe(2);

		// A late older run is created cancelled and never gets ready work.
		if (order.IndexOf('A') > order.IndexOf('C'))
		{
			(await ReadyWorkAsync(db, runs[0], ct)).ShouldBe(0);
		}

		if (order.IndexOf('B') > order.IndexOf('C'))
		{
			(await ReadyWorkAsync(db, runs[1], ct)).ShouldBe(0);
		}

		// Duplicates, including new event ids, change nothing.
		var supersessionsBefore = await SupersessionsAsync(db, ct);
		var outboxBefore = await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.outbox_messages", ct);
		foreach (var set in sets.Reverse())
		{
			await DeliverAsync(host, set, duplicates: true, ct);
		}

		(await SupersessionsAsync(db, ct)).ShouldBe(supersessionsBefore);
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.outbox_messages", ct)).ShouldBe(outboxBefore);
		(await AuditCountAsync(db, "analysis.run-superseded", null, ct)).ShouldBe(2);
		(await CancelledNotificationsAsync(db, null, ct)).ShouldBe(2);
	}

	[Fact]
	public async Task ConcurrentFinalizationsOfTwoVersionsLeaveTheOlderRunSuperseded()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 2, ct);
		var notifications = new[] { await NotificationAsync(host, sets[0], ct), await NotificationAsync(host, sets[1], ct) };

		await Task.WhenAll(notifications.Select(n => HandleAsync(host, n, ct)));

		var older = await RunOfAsync(db, sets[0], ct);
		var newer = await RunOfAsync(db, sets[1], ct);
		(await SupersessionsAsync(db, ct)).ToDictionary(r => r.Key, r => r.Value.By).ShouldBe(new Dictionary<Guid, Guid> { [older] = newer });
		(await StateAsync(db, older, ct)).ShouldBe("Cancelled/superseded");
		(await StateAsync(db, newer, ct)).ShouldBeOneOf("Requested/", "Running/");
		(await AuditCountAsync(db, "analysis.run-superseded", older, ct)).ShouldBe(1);
		(await CancelledNotificationsAsync(db, older, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task MemberCancellationBeforeSupersessionLeavesOnlyTheSupersessionRow()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 2, ct);
		await DeliverAsync(host, sets[0], duplicates: false, ct);
		var older = await RunOfAsync(db, sets[0], ct);
		var version = await ScalarAsync<long>(db, $"SELECT version FROM socalytics.analysis_runs WHERE id = '{older}'", ct);

		using (var cancel = await CancelAsync(coach, older, version, ct))
		{
			cancel.StatusCode.ShouldBe(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync(ct));
		}

		await DeliverAsync(host, sets[1], duplicates: false, ct);
		var newer = await RunOfAsync(db, sets[1], ct);

		(await StateAsync(db, older, ct)).ShouldBe("Cancelled/member");
		(await ScalarAsync<bool>(db, $"SELECT cancelled_by_account_id IS NOT NULL AND cancellation_reason = 'Wrong recordings.' FROM socalytics.analysis_runs WHERE id = '{older}'", ct)).ShouldBeTrue();
		(await SupersessionsAsync(db, ct)).ShouldBe(new Dictionary<Guid, (Guid, string)> { [older] = (newer, "Cancelled") });
		(await AuditCountAsync(db, "analysis.run-cancelled", older, ct)).ShouldBe(1);
		(await AuditCountAsync(db, "analysis.run-superseded", null, ct)).ShouldBe(0);
		(await CancelledNotificationsAsync(db, older, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task MemberCancellationAfterSupersessionAddsNoSecondCancellation()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 2, ct);
		await DeliverAsync(host, sets[0], duplicates: false, ct);
		var older = await RunOfAsync(db, sets[0], ct);
		var version = await ScalarAsync<long>(db, $"SELECT version FROM socalytics.analysis_runs WHERE id = '{older}'", ct);

		await DeliverAsync(host, sets[1], duplicates: false, ct);
		var newer = await RunOfAsync(db, sets[1], ct);
		var before = await ScalarAsync<string>(db, $"SELECT to_jsonb(r)::text FROM socalytics.analysis_runs r WHERE id = '{older}'", ct);

		using (var cancel = await CancelAsync(coach, older, version, ct))
		{
			cancel.StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed);
		}

		(await ScalarAsync<string>(db, $"SELECT to_jsonb(r)::text FROM socalytics.analysis_runs r WHERE id = '{older}'", ct)).ShouldBe(before);
		(await StateAsync(db, older, ct)).ShouldBe("Cancelled/superseded");
		(await SupersessionsAsync(db, ct)).ToDictionary(r => r.Key, r => r.Value.By).ShouldBe(new Dictionary<Guid, Guid> { [older] = newer });
		(await AuditCountAsync(db, "analysis.run-superseded", older, ct)).ShouldBe(1);
		(await AuditCountAsync(db, "analysis.run-cancelled", older, ct)).ShouldBe(0);
		(await CancelledNotificationsAsync(db, older, ct)).ShouldBe(1);
	}

	[Fact]
	public async Task RunsOfTheSameVersionNeverSupersedeEachOther()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 2, ct);
		await DeliverAsync(host, sets[0], duplicates: false, ct);
		var first = await RunOfAsync(db, sets[0], ct);
		var second = await SeedRunAsync(db, hierarchy, sets[0], await FinalizedAtAsync(host, sets[0], ct), "wf-second", "Requested", ct);

		(await ReconcileAsync(host, ct)).RunsSuperseded.ShouldBe(0);
		(await SupersessionsAsync(db, ct)).ShouldBeEmpty();
		(await StateAsync(db, first, ct)).ShouldBeOneOf("Requested/", "Running/");
		(await StateAsync(db, second, ct)).ShouldBeOneOf("Requested/", "Running/");

		await DeliverAsync(host, sets[1], duplicates: false, ct);
		var newer = await RunOfAsync(db, sets[1], ct);
		var supersessions = await SupersessionsAsync(db, ct);
		supersessions.Keys.ToHashSet().ShouldBe([first, second], ignoreOrder: true);
		supersessions.Values.ShouldAllBe(v => v.By == newer);
	}

	[Fact]
	public async Task VersionsWithEqualFinalizationTimesFollowTheIdTieBreak()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 2, ct);
		var finalizedAt = await FinalizedAtAsync(host, sets[0], ct);
		var runA = await SeedRunAsync(db, hierarchy, sets[0], finalizedAt, "wf-a", "Requested", ct);
		var runB = await SeedRunAsync(db, hierarchy, sets[1], finalizedAt, "wf-b", "Requested", ct);
		var higherSet = await ScalarAsync<Guid>(db, $"SELECT id FROM socalytics.recording_set_versions WHERE id IN ('{sets[0]}', '{sets[1]}') ORDER BY id DESC LIMIT 1", ct);
		var (current, older) = higherSet == sets[0] ? (runA, runB) : (runB, runA);

		(await ReconcileAsync(host, ct)).RunsSuperseded.ShouldBe(1);

		(await SupersessionsAsync(db, ct)).ShouldBe(new Dictionary<Guid, (Guid, string)> { [older] = (current, "Requested") });
		(await StateAsync(db, older, ct)).ShouldBe("Cancelled/superseded");
		(await StateAsync(db, current, ct)).ShouldBe("Requested/");
	}

	[Fact]
	public async Task ReconciliationSupersedesRunsInsertedWithoutSupersessionRows()
	{
		var ct = TestContext.Current.CancellationToken;
		var (host, db, coach, hierarchy) = await StartAsync(ct);
		await using var _ = host;
		var sets = await FinalizeAsync(host, coach, hierarchy, 3, ct);
		var completed = await SeedRunAsync(db, hierarchy, sets[0], await FinalizedAtAsync(host, sets[0], ct), "wf-a", "Completed", ct);
		var running = await SeedRunAsync(db, hierarchy, sets[1], await FinalizedAtAsync(host, sets[1], ct), "wf-b", "Running", ct);
		var current = await SeedRunAsync(db, hierarchy, sets[2], await FinalizedAtAsync(host, sets[2], ct), "wf-c", "Requested", ct);

		(await ReconcileAsync(host, ct)).RunsSuperseded.ShouldBe(2);

		var supersessions = await SupersessionsAsync(db, ct);
		supersessions.Count.ShouldBe(2);
		supersessions[completed].ShouldBe((current, "Completed"));
		supersessions[running].ShouldBe((current, "Running"));
		(await StateAsync(db, completed, ct)).ShouldBe("Completed/");
		(await StateAsync(db, running, ct)).ShouldBe("Cancelled/superseded");
		(await StateAsync(db, current, ct)).ShouldBe("Requested/");
		(await AuditCountAsync(db, "analysis.run-superseded", running, ct)).ShouldBe(1);
		(await AuditCountAsync(db, "analysis.run-superseded", completed, ct)).ShouldBe(0);
		(await CancelledNotificationsAsync(db, running, ct)).ShouldBe(1);

		var outboxBefore = await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed'", ct);
		var auditBefore = await AuditCountAsync(db, "analysis.run-superseded", null, ct);

		(await ReconcileAsync(host, ct)).RunsSuperseded.ShouldBe(0);

		(await SupersessionsAsync(db, ct)).OrderBy(r => r.Key).ShouldBe(supersessions.OrderBy(r => r.Key));
		(await ScalarAsync<long>(db, "SELECT count(*) FROM socalytics.outbox_messages WHERE message_type = 'analysis.run-state-changed'", ct)).ShouldBe(outboxBefore);
		(await AuditCountAsync(db, "analysis.run-superseded", null, ct)).ShouldBe(auditBefore);
	}
}

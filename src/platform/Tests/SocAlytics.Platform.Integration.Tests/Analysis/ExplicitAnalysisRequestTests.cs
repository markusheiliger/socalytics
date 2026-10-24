using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Recordings;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

// User Story 1: an explicit analysis run request creates the run the finalization path would create.
[Collection("NatsFaultInjection")]
public sealed class ExplicitAnalysisRequestTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private static Dictionary<string, string?> Workflow(string videoPolicy, int? segmentationPolicyVersion = null)
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
		{
			dir = dir.Parent;
		}

		var cfg = new ConfigurationBuilder()
			.AddJsonFile(Path.Combine(dir.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")).Build();
		var values = cfg.GetSection("Analysis").AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
		values["Analysis:DefaultWorkflow:Nodes:0:VideoPolicy"] = videoPolicy;
		if (segmentationPolicyVersion is { } version)
		{
			values["Analysis:DefaultWorkflow:SegmentationPolicy:Version"] = version.ToString();
		}

		return values;
	}

	private static string RunsPath(Guid matchId) => $"/api/v1/matches/{matchId}/analysis-runs";

	private static async Task<HttpResponseMessage> RequestAsync(
		HttpClient client, Guid matchId, string? antiforgery, string? key, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, RunsPath(matchId));
		if (antiforgery is not null)
		{
			request.Headers.Add("X-CSRF-Token", antiforgery);
		}

		if (key is not null)
		{
			request.Headers.Add("Idempotency-Key", key);
		}

		return await client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> AcceptAsync(ApiSession session, Guid matchId, CancellationToken ct)
	{
		using var response = await RequestAsync(session.Client, matchId, session.AntiforgeryToken, Guid.NewGuid().ToString(), ct);
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Accepted, text);
		var body = JsonDocument.Parse(text).RootElement.Clone();
		response.Headers.Location!.ToString().ShouldBe($"/api/v1/analysis/runs/{body.GetProperty("runId").GetGuid()}");
		return body;
	}

	private static async Task<string> TextAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? string.Empty;
	}

	private static async Task ExecuteAsync(FinalizationSetup s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static async Task<NotificationOutcome> NotifyAsync(FinalizationSetup s, Guid setVersion, CancellationToken ct)
	{
		await using var scope = s.Factory.Services.CreateAsyncScope();
		var lineage = (await scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>().GetAsync(setVersion, ct))!;
		var notification = new RecordingsFinalizedNotification(
			Guid.NewGuid(), "1.0.0", DateTimeOffset.UtcNow, lineage.RecordingSetVersionId, lineage.MatchId, lineage.TeamId,
			lineage.Members.Select(m => new FinalizedNotificationMember(
				m.Position, m.RecordingVersionId, m.RecordingContentDigest, m.TimelineMappingId, m.TimelineMappingDigest)).ToList());
		return await scope.ServiceProvider.GetRequiredService<HandleRecordingsFinalizedNotificationHandler>().HandleAsync(notification, ct);
	}

	private static async Task<FinalizedRecordingSet> FinalizeAsync(FinalizationSetup s, int seed, CancellationToken ct)
	{
		s.Factory.Time.Advance(TimeSpan.FromMinutes(1));
		return await RecordingFinalizationHelper.FinalizeAsync(s.Coach, s.Hierarchy.MatchId, 1, ct);
	}

	// Everything the creation path decides, without identifiers, so that two stamps can be compared.
	private static async Task<string> ShapeAsync(FinalizationSetup s, CancellationToken ct)
	{
		var parts = new[]
		{
			"SELECT string_agg(state || '|' || workflow_id || '|' || workflow_version || '|' || requested_capabilities::text || '|' || completion_policy, ';') FROM socalytics.analysis_runs",
			"SELECT string_agg(snapshot_digest, ';') FROM socalytics.analysis_run_snapshots",
			"SELECT string_agg(node_key || '|' || capability_id || '|' || execution_scope || '|' || state, ';' ORDER BY ordinal) FROM socalytics.analysis_workflow_nodes",
			"SELECT count(*)::text FROM socalytics.analysis_dependency_edges",
			"SELECT string_agg(n.node_key || '|' || COALESCE(j.segment_number::text, '-') || '|' || j.state || '|' || COALESCE(j.segment_rejection_reason, '-') || '|' || COALESCE(j.failure_reason, '-'), ';' ORDER BY n.node_key, j.segment_number) " +
				"FROM socalytics.analysis_logical_jobs j JOIN socalytics.analysis_workflow_nodes n ON n.id = j.node_id",
			"SELECT count(*)::text FROM socalytics.outbox_messages WHERE message_type = 'analysis.ready-work'",
			"SELECT count(*)::text || '|' || count(*) FILTER (WHERE demanded)::text FROM socalytics.materialized_segments",
		};
		var shape = new List<string>();
		foreach (var sql in parts)
		{
			shape.Add(await TextAsync(s, sql, ct));
		}

		return string.Join("\n", shape);
	}

	private static async Task<ApiSession> SeedRoleMemberAsync(FinalizationSetup s, string name, string? role, CancellationToken ct)
	{
		var id = await TestMembers.SeedAsync(s.Db, name, cancellationToken: ct);
		if (role is not null)
		{
			await ExecuteAsync(
				s,
				$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{id}', '{s.Hierarchy.TeamId}', '{role}', now(), '{id}')",
				ct);
		}

		return await ApiSession.SignInAsync(s.Factory, name, TestMembers.DefaultPassword, ct);
	}

	private async Task<(string Shape, string Rejection)> FinalizationShapeAsync(string videoPolicy, int? policyVersion, CancellationToken ct)
	{
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, extraSettings: Workflow(videoPolicy, policyVersion));
		var set = await FinalizeAsync(s, 1, ct);
		(await NotifyAsync(s, set.RecordingSetVersionId, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>();
		return (await ShapeAsync(s, ct), await TextAsync(s, "SELECT count(*)::text FROM socalytics.analysis_logical_jobs WHERE segment_rejection_reason IS NOT NULL", ct));
	}

	private async Task<(string Shape, string Rejection)> RequestShapeAsync(string videoPolicy, int? policyVersion, CancellationToken ct)
	{
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct, extraSettings: Workflow(videoPolicy, policyVersion));
		await FinalizeAsync(s, 1, ct);
		await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);
		return (await ShapeAsync(s, ct), await TextAsync(s, "SELECT count(*)::text FROM socalytics.analysis_logical_jobs WHERE segment_rejection_reason IS NOT NULL", ct));
	}

	[Fact]
	public async Task ClubAdminAndCoachGetANewRunForTheCurrentSetAndResolvedWorkflow()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var set = await FinalizeAsync(s, 1, ct);

		var coach = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);
		coach.GetProperty("created").GetBoolean().ShouldBeTrue();
		coach.GetProperty("recordingSetVersionId").GetGuid().ShouldBe(set.RecordingSetVersionId);
		coach.GetProperty("state").GetString().ShouldNotBeNullOrEmpty();
		coach.GetProperty("state").GetString().ShouldNotBe("Failed");
		coach.GetProperty("currentRunId").ValueKind.ShouldBe(JsonValueKind.Null);
		var runId = coach.GetProperty("runId").GetGuid();
		coach.GetProperty("statusLink").GetString().ShouldBe($"/api/v1/analysis/runs/{runId}");
		(await TextAsync(s, $"SELECT recording_set_version_id::text || '|' || workflow_id || '|' || workflow_version FROM socalytics.analysis_runs WHERE id = '{runId}'", ct))
			.ShouldBe($"{set.RecordingSetVersionId}|{coach.GetProperty("workflowId").GetString()}|{coach.GetProperty("workflowVersion").GetString()}");

		// The same inputs for the Club Admin find the existing run instead of creating another one.
		var admin = await AcceptAsync(s.Admin, s.Hierarchy.MatchId, ct);
		admin.GetProperty("runId").GetGuid().ShouldBe(runId);
		admin.GetProperty("created").GetBoolean().ShouldBeFalse();
	}

	[Fact]
	public async Task ClubAdminGetsAcceptedRunForAFreshMatch()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		await FinalizeAsync(s, 1, ct);

		var admin = await AcceptAsync(s.Admin, s.Hierarchy.MatchId, ct);

		admin.GetProperty("created").GetBoolean().ShouldBeTrue();
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task RequestedRunEqualsTheFinalizationPathRunForTheSameInputs()
	{
		var ct = TestContext.Current.CancellationToken;

		var finalization = await FinalizationShapeAsync("required", null, ct);
		var request = await RequestShapeAsync("required", null, ct);

		request.Shape.ShouldBe(finalization.Shape);
		request.Shape.ShouldContain("segment");
	}

	[Fact]
	public async Task CurrentLookupEqualsTheLineageOfTheFinalizedVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var set = await FinalizeAsync(s, 1, ct);

		await using var scope = s.Factory.Services.CreateAsyncScope();
		var lookup = scope.ServiceProvider.GetRequiredService<IRecordingSetLookup>();
		var byVersion = await lookup.GetAsync(set.RecordingSetVersionId, ct);
		var current = await lookup.GetCurrentForMatchAsync(s.Hierarchy.MatchId, ct);

		current.ShouldNotBeNull();
		current.ShouldBeEquivalentTo(byVersion);
	}

	[Fact]
	public async Task OlderRunsAreSupersededWithTheNewRunAsSuccessor()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var older = await FinalizeAsync(s, 1, ct);
		var olderRun = (await NotifyAsync(s, older.RecordingSetVersionId, ct)).ShouldBeOfType<NotificationOutcome.RunCreated>().RunId;
		var newer = await FinalizeAsync(s, 2, ct);

		var accepted = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);

		accepted.GetProperty("recordingSetVersionId").GetGuid().ShouldBe(newer.RecordingSetVersionId);
		var newRun = accepted.GetProperty("runId").GetGuid();
		newRun.ShouldNotBe(olderRun);
		(await TextAsync(s, $"SELECT superseding_run_id::text FROM socalytics.analysis_run_supersessions WHERE run_id = '{olderRun}'", ct))
			.ShouldBe(newRun.ToString());
		(await TextAsync(s, $"SELECT state FROM socalytics.analysis_runs WHERE id = '{olderRun}'", ct)).ShouldBe("Cancelled");
	}

	[Fact]
	public async Task ARequestWritesExactlyOneAuditEventReceiptAndRequestRecord()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		await FinalizeAsync(s, 1, ct);

		var accepted = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run.request'", ct)).ShouldBe(1);
		(await TextAsync(
			s,
			"SELECT actor_account_id::text || '|' || resource_type || '|' || resource_id || '|' || (details->>'outcome') || '|' || (details->>'matchId') FROM socalytics.security_audit_event WHERE event_type = 'analysis.run.request'",
			ct)).ShouldBe($"{s.CoachId}|analysis-run|{accepted.GetProperty("runId").GetGuid()}|run-created|{s.Hierarchy.MatchId}");
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE message_type = 'analysis-requested'", ct)).ShouldBe(1);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_run_requests", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task ASingleRequestIsAnsweredSynchronouslyWithinTheCeiling()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		await FinalizeAsync(s, 1, ct);

		var watch = Stopwatch.StartNew();
		var accepted = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);
		watch.Stop();

		watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
		accepted.GetProperty("created").GetBoolean().ShouldBeTrue();
	}

	[Fact]
	public async Task DeniedCallersCreateNoRunReceiptRequestRecordOrAuditEvent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		await FinalizeAsync(s, 1, ct);
		var viewer = await SeedRoleMemberAsync(s, "viewer-1", "viewer", ct);
		var outsider = await SeedRoleMemberAsync(s, "outsider-1", null, ct);
		using var anonymous = s.Factory.CreateApiClient();

		async Task AssertDeniedAsync(string name, HttpClient client, string? antiforgery, HttpStatusCode expected)
		{
			using var response = await RequestAsync(client, s.Hierarchy.MatchId, antiforgery, Guid.NewGuid().ToString(), ct);
			response.StatusCode.ShouldBe(expected, $"{name}: {await response.Content.ReadAsStringAsync(ct)}");
			(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(0, name);
			(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE message_type = 'analysis-requested'", ct)).ShouldBe(0, name);
			(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_run_requests", ct)).ShouldBe(0, name);
			(await s.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run.request'", ct)).ShouldBe(0, name);
		}

		await AssertDeniedAsync("viewer", viewer.Client, viewer.AntiforgeryToken, HttpStatusCode.Forbidden);
		await AssertDeniedAsync("member without a role", outsider.Client, outsider.AntiforgeryToken, HttpStatusCode.NotFound);
		await AssertDeniedAsync("unauthenticated", anonymous, null, HttpStatusCode.Unauthorized);
	}

	[Fact]
	public async Task MissingOrInvalidAntiforgeryTokenIsRefusedWithoutEffects()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		await FinalizeAsync(s, 1, ct);

		foreach (var token in new string?[] { null, "invalid-token" })
		{
			using var response = await RequestAsync(s.Coach.Client, s.Hierarchy.MatchId, token, Guid.NewGuid().ToString(), ct);
			response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await response.Content.ReadAsStringAsync(ct));
		}

		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(0);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_notification_receipts WHERE message_type = 'analysis-requested'", ct)).ShouldBe(0);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_run_requests", ct)).ShouldBe(0);
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'analysis.run.request'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task AnUnfinalizedLaterRevisionDoesNotChangeTheAnalyzedVersion()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var set = await FinalizeAsync(s, 1, ct);
		var original = await TextAsync(s, $"SELECT mapping_digest FROM socalytics.recording_timeline_mappings WHERE id = '{set.TimelineMappingIds[0]}'", ct);

		using (var revision = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/matches/{s.Hierarchy.MatchId}/recording-versions/{set.RecordingVersionIds[0]}/timeline-mappings")
		{
			Content = JsonContent.Create(new { spans = new[] { new { mediaStartSeconds = 0, mediaEndSeconds = 10, matchStartSeconds = 7 } } }),
		})
		{
			revision.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
			revision.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
			using var revised = await s.Coach.Client.SendAsync(revision, ct);
			revised.StatusCode.ShouldBe(HttpStatusCode.Created, await revised.Content.ReadAsStringAsync(ct));
		}

		var accepted = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);

		accepted.GetProperty("recordingSetVersionId").GetGuid().ShouldBe(set.RecordingSetVersionId);
		(await TextAsync(s, "SELECT string_agg(DISTINCT timeline_mapping_digest, ';') FROM socalytics.analysis_logical_jobs WHERE timeline_mapping_digest IS NOT NULL", ct))
			.ShouldBe(original);
	}

	[Fact]
	public async Task ARequestBeforeTheFinalizationNotificationIsProcessedLetsTheNotificationReturnTheRun()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var s = await FinalizationSetup.StartAsync(postgres, rustFs, ct);
		var set = await FinalizeAsync(s, 1, ct);

		var accepted = await AcceptAsync(s.Coach, s.Hierarchy.MatchId, ct);
		var outcome = await NotifyAsync(s, set.RecordingSetVersionId, ct);

		outcome.ShouldBeOfType<NotificationOutcome.RunExists>().RunId.ShouldBe(accepted.GetProperty("runId").GetGuid());
		(await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_runs", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task RejectedSegmentRequestRecordsTheSamePerJobReasonAsTheFinalizationPath()
	{
		var ct = TestContext.Current.CancellationToken;

		// An unsupported segmentation policy version makes the materialization request reject every segment.
		var finalization = await FinalizationShapeAsync("required", 2, ct);
		var request = await RequestShapeAsync("required", 2, ct);

		finalization.Rejection.ShouldNotBe("0");
		request.Rejection.ShouldBe(finalization.Rejection);
		request.Shape.ShouldBe(finalization.Shape);
	}
}

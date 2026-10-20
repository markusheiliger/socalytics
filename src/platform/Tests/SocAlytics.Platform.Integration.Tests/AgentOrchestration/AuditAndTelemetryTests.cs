using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// FR-034, FR-034a, SC-006: one identifier-only audit event per outcome, bounded metrics, and no content in audit, logs, metrics, or outbox.
public sealed class AuditAndTelemetryTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "PRIVATE-QUESTION-8f31 how do we beat the high press?";
	private const string Advice = "PRIVATE-ADVICE-77c2 switch to a back three.";
	private const string Note = "PRIVATE-NOTE-19ab halfway through the film.";

	private static readonly string[] Secrets = ["PRIVATE-QUESTION-8f31", "PRIVATE-ADVICE-77c2", "PRIVATE-NOTE-19ab"];

	private static readonly object References = new
	{
		runtime = "test-runtime",
		modelProvider = "test-provider",
		model = "test-model",
		deployment = "test-deployment",
		promptRef = "prompt-1",
		toolPolicyRef = "tools-1",
		safetyPolicyRef = "safety-1",
	};

	private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

	private static MeterListener Listen(List<Measurement> sink)
	{
		var listener = new MeterListener
		{
			InstrumentPublished = (instrument, l) =>
			{
				if (instrument.Meter.Name == AgentOrchestrationMetrics.MeterName)
				{
					l.EnableMeasurementEvents(instrument);
				}
			},
		};
		listener.SetMeasurementEventCallback<long>((i, v, t, _) =>
		{
			lock (sink)
			{
				sink.Add(new Measurement(i.Name, v, t.ToArray().ToDictionary(x => x.Key, x => x.Value)));
			}
		});
		listener.Start();
		return listener;
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<HttpResponseMessage> PostAsync(
		ApiSession session, string path, object body, string? key, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await session.Client.SendAsync(request, ct);
	}

	private static async Task<Guid> ConversationAsync(AgentOrchestrationFixture f, ApiSession owner, CancellationToken ct)
	{
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await PostAsync(owner, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = run }, null, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var id = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await PostAsync(owner, $"/api/v1/agent-conversations/{id}/participants", new { agentId = Tactics }, null, created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		return id;
	}

	private static async Task<Guid> SubmitAsync(
		ApiSession owner, Guid conversationId, string? key, string agent, HttpStatusCode expected, CancellationToken ct)
	{
		using var response = await PostAsync(owner, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = agent, question = Question }, key, null, ct);
		response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(ct));
		var body = await ReadAsync(response, ct);
		return body.TryGetProperty("invocationId", out var id) ? id.GetGuid() : Guid.Empty;
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static async Task ClaimRefusedAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync(ct));
	}

	private static async Task<T> InScopeAsync<T>(AgentOrchestrationFixture f, Func<IServiceProvider, Task<T>> action)
	{
		await using var scope = f.Factory.Services.CreateAsyncScope();
		return await action(scope.ServiceProvider);
	}

	private static Task<AgentMonitorResult> MonitorAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		InScopeAsync(f, sp => sp.GetRequiredService<MonitorAgentAttemptsHandler>().HandleAsync(ct));

	private static Task<long> AuditAsync(AgentOrchestrationFixture f, string eventType, string outcome, string actorKind, CancellationToken ct) =>
		f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{eventType}' AND outcome = '{outcome}' AND actor_kind = '{actorKind}'", ct);

	private static long Sum(List<Measurement> snapshot, string instrument, string? tag = null, string? value = null) =>
		snapshot.Where(m => m.Instrument == instrument && (tag is null || (m.Tags.TryGetValue(tag, out var v) && (string?)v == value))).Sum(m => m.Value);

	[Fact]
	public async Task EveryOutcomeIsAuditedAndCountedWithoutAnyContent()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, s => s["Analysis:Workers:Enabled"] = "false");
		var sink = new List<Measurement>();
		using var listener = Listen(sink);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);

		// Conversation, participant, and the accepted, replayed, and refused submissions.
		var conversation = await ConversationAsync(f, f.Admin, ct);
		var first = await SubmitAsync(f.Admin, conversation, "submit-1", Tactics, HttpStatusCode.Accepted, ct);
		(await SubmitAsync(f.Admin, conversation, "submit-1", Tactics, HttpStatusCode.Accepted, ct)).ShouldBe(first);
		await SubmitAsync(f.Admin, conversation, "submit-2", "specialist-set-pieces", HttpStatusCode.Conflict, ct);

		// Granted claim, refused claim (attempt active), checkpoint, and completion.
		var envelope = await ClaimAsync(f, token, first, ct);
		await ClaimRefusedAsync(f, token, first, ct);
		var attemptId = envelope.GetProperty("attemptId").GetGuid();
		var fencing = envelope.GetProperty("fencingToken").GetString();
		using (var checkpoint = await f.Workload.CheckpointAsync(
			f.Anon, f.Now, token, attemptId, new { operationId = "cp-1", fencingToken = fencing, kind = "progress", note = Note }, ct))
		{
			checkpoint.StatusCode.ShouldBe(HttpStatusCode.OK, await checkpoint.Content.ReadAsStringAsync(ct));
		}

		using (var completed = await f.Workload.CompleteAsync(
			f.Anon, f.Now, token, attemptId, new { operationId = "done-1", fencingToken = fencing, advice = Advice, executionReferences = References }, ct))
		{
			completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));
		}

		// A claim of the ended invocation is refused; a non-retryable failure ends another invocation.
		await ClaimRefusedAsync(f, token, first, ct);
		var failing = await SubmitAsync(f.Admin, conversation, "submit-3", Tactics, HttpStatusCode.Accepted, ct);
		var failingEnvelope = await ClaimAsync(f, token, failing, ct);
		using (var failed = await f.Workload.FailAsync(
			f.Anon, f.Now, token, failingEnvelope.GetProperty("attemptId").GetGuid(),
			new { operationId = "fail-1", fencingToken = failingEnvelope.GetProperty("fencingToken").GetString(), reasonCode = "model-error", retryable = false }, ct))
		{
			failed.StatusCode.ShouldBe(HttpStatusCode.OK, await failed.Content.ReadAsStringAsync(ct));
		}

		// Three lease expiries exhaust the attempts.
		var exhausting = await SubmitAsync(f.Admin, conversation, "submit-4", Tactics, HttpStatusCode.Accepted, ct);
		for (var attempt = 1; attempt <= 3; attempt++)
		{
			await ClaimAsync(f, token, exhausting, ct);
			f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
			(await MonitorAsync(f, ct)).Expired.ShouldBe(1);
		}

		// An owner who loses the team role loses the running attempt and the queued claim.
		var coachId = await TestMembers.SeedAsync(f.Db, "coach-1", cancellationToken: ct);
		await f.ScalarAsync(
			$"INSERT INTO socalytics.team_role_assignment (member_account_id, team_id, role, assigned_at, assigned_by_account_id) VALUES ('{coachId}', '{f.Hierarchy.TeamId}', 'coach', now(), '{coachId}')", ct);
		using var coach = await ApiSession.SignInAsync(f.Factory, "coach-1", TestMembers.DefaultPassword, ct);
		var coachConversation = await ConversationAsync(f, coach, ct);
		var running = await SubmitAsync(coach, coachConversation, "coach-1", Tactics, HttpStatusCode.Accepted, ct);
		var queued = await SubmitAsync(coach, coachConversation, "coach-2", Tactics, HttpStatusCode.Accepted, ct);
		await ClaimAsync(f, token, running, ct);
		await f.ScalarAsync($"UPDATE socalytics.team_role_assignment SET role = 'viewer' WHERE member_account_id = '{coachId}'", ct);
		(await MonitorAsync(f, ct)).ScopeLost.ShouldBe(1);
		await ClaimRefusedAsync(f, token, queued, ct);

		// One invocation stays queued for the gauge.
		await SubmitAsync(f.Admin, conversation, "submit-5", Tactics, HttpStatusCode.Accepted, ct);
		await MonitorAsync(f, ct);
		listener.RecordObservableInstruments();

		// Audit: one event per outcome with the right actor and outcome.
		(await AuditAsync(f, "agent.conversation.created", "succeeded", "member", ct)).ShouldBe(2);
		(await AuditAsync(f, "agent.participant.added", "succeeded", "member", ct)).ShouldBe(2);
		(await AuditAsync(f, "agent.invocation.submitted", "succeeded", "member", ct)).ShouldBe(6);
		(await AuditAsync(f, "agent.invocation.submitted", "unchanged", "member", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.invocation.submitted", "refused", "member", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.attempt.claimed", "succeeded", "agent-workload", ct)).ShouldBe(6);
		(await AuditAsync(f, "agent.attempt.claimed", "refused", "agent-workload", ct)).ShouldBe(2);
		(await AuditAsync(f, "agent.attempt.completed", "succeeded", "agent-workload", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.attempt.failed", "failed", "agent-workload", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.attempt.expired", "failed", "system", ct)).ShouldBe(3);
		(await AuditAsync(f, "agent.invocation.exhausted", "failed", "system", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.attempt.scope-lost", "refused", "system", ct)).ShouldBe(1);
		(await AuditAsync(f, "agent.attempt.scope-lost", "refused", "agent-workload", ct)).ShouldBe(1);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.invocation.submitted' AND outcome = 'succeeded' AND actor_account_id IS NULL", ct))
			.ShouldBe(0);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.invocation.submitted' AND outcome = 'succeeded' AND details ->> 'invocationId' = '{first}' AND details ->> 'conversationId' = '{conversation}' AND details ->> 'agentId' = '{Tactics}'", ct))
			.ShouldBe(1);
		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.attempt.claimed' AND outcome = 'succeeded' AND details ->> 'invocationId' = '{first}' AND details ->> 'attemptId' = '{attemptId}' AND details ->> 'workloadClientId' = '{f.Workload.ClientId}'", ct))
			.ShouldBe(1);

		// Metrics move as expected and never carry anything but the bounded tags.
		List<Measurement> snapshot;
		lock (sink)
		{
			snapshot = sink.ToList();
		}

		// The meter is process-wide, so concurrent tests can only add to these lower bounds.
		Sum(snapshot, "socalytics.agents.submissions", "outcome", "accepted").ShouldBeGreaterThanOrEqualTo(6);
		Sum(snapshot, "socalytics.agents.submissions", "outcome", "replayed").ShouldBeGreaterThanOrEqualTo(1);
		Sum(snapshot, "socalytics.agents.submissions", "outcome", "refused").ShouldBeGreaterThanOrEqualTo(1);
		Sum(snapshot, "socalytics.agents.claims", "outcome", "granted").ShouldBeGreaterThanOrEqualTo(6);
		Sum(snapshot, "socalytics.agents.claims", "outcome", "refused").ShouldBeGreaterThanOrEqualTo(2);
		Sum(snapshot, "socalytics.agents.completions", "outcome", "accepted").ShouldBeGreaterThanOrEqualTo(1);
		Sum(snapshot, "socalytics.agents.failures").ShouldBeGreaterThanOrEqualTo(2);
		Sum(snapshot, "socalytics.agents.lease_expiries").ShouldBeGreaterThanOrEqualTo(3);
		Sum(snapshot, "socalytics.agents.scope_losses").ShouldBeGreaterThanOrEqualTo(2);
		snapshot.ShouldContain(m => m.Instrument == "socalytics.agents.invocations_queued");

		var allowedTags = new HashSet<string> { "outcome", "failure_reason", "reason" };
		foreach (var measurement in snapshot)
		{
			measurement.Tags.Keys.ShouldBeSubsetOf(allowedTags);
			foreach (var value in measurement.Tags.Values.Select(v => v?.ToString() ?? string.Empty))
			{
				value.Length.ShouldBeLessThan(40);
				Guid.TryParse(value, out _).ShouldBeFalse();
				Secrets.ShouldNotContain(secret => value.Contains(secret, StringComparison.Ordinal));
			}
		}

		// No audit event, outbox message, log entry, or metric tag carries submitted text.
		var audit = await TextAsync(f, "SELECT coalesce(string_agg(event_type || action || outcome || coalesce(reason_code, '') || details::text, ' '), '') FROM socalytics.security_audit_event", ct);
		var outbox = await TextAsync(f, "SELECT coalesce(string_agg(payload::text, ' '), '') FROM socalytics.outbox_messages", ct);
		outbox.ShouldContain(first.ToString());
		var logs = string.Join(
			' ',
			f.Factory.Logs.Entries.Select(e => e.Message + " " + string.Join(' ', e.State.Select(s => s.Key + "=" + s.Value)) + " " + e.Exception));
		foreach (var secret in Secrets)
		{
			audit.ShouldNotContain(secret);
			outbox.ShouldNotContain(secret);
			logs.ShouldNotContain(secret);
		}
	}

	private static async Task<string> TextAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new Npgsql.NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new Npgsql.NpgsqlCommand(sql, connection);
		return (string)(await command.ExecuteScalarAsync(ct))!;
	}
}

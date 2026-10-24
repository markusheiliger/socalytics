using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.AgentOrchestration.Execution;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// SC-002: the purge of a deleted conversation removes its content, is idempotent, and never leaks text.
[Collection("NatsFaultInjection")]
public sealed class ConversationPurgeTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
	private const string Tactics = "specialist-tactics";
	private const string Question = "PRIVATE-QUESTION-5c1e how do we break the low block?";
	private const string Advice = "PRIVATE-ADVICE-90d4 overload the weak side.";

	private static readonly string[] Secrets = ["PRIVATE-QUESTION-5c1e", "PRIVATE-ADVICE-90d4"];

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

	private sealed record Reply(HttpStatusCode Status, string Text, string? ETag);

	private static Task<AgentOrchestrationFixture> StartAsync(
		PostgresContainerFixture postgres, CancellationToken ct, Action<Dictionary<string, string?>>? settings = null) =>
		AgentOrchestrationFixture.StartAsync(
			postgres, ct,
			s =>
			{
				s["Analysis:Workers:Enabled"] = "false";
				settings?.Invoke(s);
			});

	private static async Task<Reply> SendAsync(
		ApiSession session, HttpMethod method, string path, object? body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", session.AntiforgeryToken);
		}

		if (method == HttpMethod.Post)
		{
			request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		using var response = await session.Client.SendAsync(request, ct);
		return new(response.StatusCode, await response.Content.ReadAsStringAsync(ct), response.Headers.ETag?.Tag);
	}

	private static JsonElement Json(Reply reply) => JsonDocument.Parse(reply.Text).RootElement.Clone();

	// A conversation with one question answered by the workload, returning its id, the invocation, and the current version.
	private static async Task<(Guid Id, Guid Invocation, string ETag)> AnsweredConversationAsync(
		AgentOrchestrationFixture f, CancellationToken ct)
	{
		var run = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		var created = await SendAsync(f.Admin, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = run }, null, ct);
		created.Status.ShouldBe(HttpStatusCode.Created, created.Text);
		var id = Json(created).GetProperty("conversationId").GetGuid();
		var added = await SendAsync(f.Admin, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/participants", new { agentId = Tactics }, created.ETag, ct);
		added.Status.ShouldBe(HttpStatusCode.OK, added.Text);
		var submitted = await SendAsync(f.Admin, HttpMethod.Post, $"/api/v1/agent-conversations/{id}/invocations", new { agentId = Tactics, question = Question }, null, ct);
		submitted.Status.ShouldBe(HttpStatusCode.Accepted, submitted.Text);
		var invocation = Json(submitted).GetProperty("invocationId").GetGuid();

		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocation, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var claimText = await claim.Content.ReadAsStringAsync(ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Created, claimText);
		var envelope = JsonDocument.Parse(claimText).RootElement;
		using var completed = await f.Workload.CompleteAsync(
			f.Anon, f.Now, token, envelope.GetProperty("attemptId").GetGuid(),
			new
			{
				operationId = "complete-" + Guid.NewGuid().ToString("N"),
				fencingToken = envelope.GetProperty("fencingToken").GetString(),
				advice = Advice,
				executionReferences = References,
			}, ct);
		completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));

		return (id, invocation, added.ETag!);
	}

	private static async Task<IReadOnlyList<Reply>> DeleteAsync(AgentOrchestrationFixture f, Guid id, string etag, CancellationToken ct)
	{
		var path = $"/api/v1/agent-conversations/{id}";
		var missing = await SendAsync(f.Admin, HttpMethod.Delete, path, null, null, ct);
		missing.Status.ShouldBe((HttpStatusCode)428, missing.Text);
		var stale = await SendAsync(f.Admin, HttpMethod.Delete, path, null, "\"999\"", ct);
		stale.Status.ShouldBe(HttpStatusCode.PreconditionFailed, stale.Text);
		var deleted = await SendAsync(f.Admin, HttpMethod.Delete, path, null, etag, ct);
		deleted.Status.ShouldBe(HttpStatusCode.NoContent, deleted.Text);
		var turns = await SendAsync(f.Admin, HttpMethod.Get, path + "/turns", null, null, ct);
		turns.Status.ShouldBe(HttpStatusCode.NotFound, turns.Text);
		return [missing, stale, turns];
	}

	private static async Task<T> InScopeAsync<T>(AgentOrchestrationFixture f, Func<IServiceProvider, Task<T>> action)
	{
		await using var scope = f.Factory.Services.CreateAsyncScope();
		return await action(scope.ServiceProvider);
	}

	private static Task<int> PurgeAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		InScopeAsync(f, sp => sp.GetRequiredService<PurgeAgentConversationHandler>().HandleAsync(ct));

	private static Task<int> ReconcileAsync(AgentOrchestrationFixture f, CancellationToken ct) =>
		InScopeAsync(f, sp => sp.GetRequiredService<ReconcileAgentWorkHandler>().HandleAsync(ct));

	private static async Task ExecuteAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await command.ExecuteNonQueryAsync(ct);
	}

	private static async Task<string> TextAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		return Convert.ToString(await command.ExecuteScalarAsync(ct)) ?? string.Empty;
	}

	private static Task<long> CountAsync(AgentOrchestrationFixture f, string table, CancellationToken ct) =>
		f.ScalarAsync($"SELECT count(*) FROM socalytics.{table}", ct);

	private static Task<string> PurgeStateAsync(AgentOrchestrationFixture f, Guid id, string copy, CancellationToken ct) =>
		TextAsync(
			f,
			$"SELECT state || '/' || coalesce(outcome, '') FROM socalytics.agent_conversation_purges WHERE conversation_id = '{id}' AND copy = '{copy}'",
			ct);

	private static async Task ShouldHoldNoTextAsync(AgentOrchestrationFixture f, IEnumerable<string> bodies, CancellationToken ct)
	{
		var audit = await TextAsync(
			f,
			"SELECT coalesce(string_agg(event_type || action || outcome || coalesce(reason_code, '') || details::text, ' '), '') FROM socalytics.security_audit_event",
			ct);
		var outbox = await TextAsync(f, "SELECT coalesce(string_agg(payload::text, ' '), '') FROM socalytics.outbox_messages", ct);
		var logs = string.Join(
			' ', f.Factory.Logs.Entries.Select(e => e.Message + " " + string.Join(' ', e.State.Select(s => s.Key + "=" + s.Value)) + " " + e.Exception));
		var problems = string.Join(' ', bodies);
		foreach (var secret in Secrets)
		{
			audit.ShouldNotContain(secret);
			outbox.ShouldNotContain(secret);
			logs.ShouldNotContain(secret);
			problems.ShouldNotContain(secret);
		}
	}

	[Fact]
	public async Task PurgeRemovesQuestionsAdviceEvidenceAndLineageAndEndsCompletedWithoutLeakingText()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, ct);
		var (id, invocation, etag) = await AnsweredConversationAsync(f, ct);
		(await CountAsync(f, "agent_accepted_advice", ct)).ShouldBe(1);
		(await CountAsync(f, "agent_lineage_entries", ct)).ShouldBeGreaterThan(0);
		(await CountAsync(f, "agent_workload_operations", ct)).ShouldBeGreaterThan(0);

		var problems = await DeleteAsync(f, id, etag, ct);

		// The tombstone hides the conversation at once, while the content waits for the purge.
		(await CountAsync(f, "agent_accepted_advice", ct)).ShouldBe(1);
		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("pending/");
		(await PurgeStateAsync(f, id, "index", ct)).ShouldBe("pending/");

		(await PurgeAsync(f, ct)).ShouldBe(2);

		(await CountAsync(f, "agent_accepted_advice", ct)).ShouldBe(0);
		(await CountAsync(f, "agent_lineage_entries", ct)).ShouldBe(0);
		(await CountAsync(f, "agent_workload_operations", ct)).ShouldBe(0);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocation}' AND question = '' AND purged_at IS NOT NULL", ct)).ShouldBe(1);
		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("completed/purged");
		(await PurgeStateAsync(f, id, "index", ct)).ShouldBe("completed/not-present");
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.conversation.purge-completed' AND resource_id = '{id}'", ct)).ShouldBe(2);

		(await PurgeAsync(f, ct)).ShouldBe(0);
		await ShouldHoldNoTextAsync(f, problems.Select(p => p.Text), ct);
	}

	[Fact]
	public async Task InterruptedPurgeIsRetriedIdempotently()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(postgres, ct);
		var (id, invocation, etag) = await AnsweredConversationAsync(f, ct);
		await DeleteAsync(f, id, etag, ct);
		(await PurgeAsync(f, ct)).ShouldBe(2);

		// The purge was interrupted after removing the content but before the record was completed.
		await ExecuteAsync(
			f,
			$"UPDATE socalytics.agent_conversation_purges SET state = 'pending', outcome = NULL, completed_at = NULL WHERE conversation_id = '{id}' AND copy = 'primary'",
			ct);
		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("pending/");

		(await PurgeAsync(f, ct)).ShouldBe(1);

		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("completed/not-present");
		(await PurgeStateAsync(f, id, "index", ct)).ShouldBe("completed/not-present");
		(await CountAsync(f, "agent_accepted_advice", ct)).ShouldBe(0);
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_invocations WHERE id = '{invocation}' AND question = ''", ct)).ShouldBe(1);
	}

	[Fact]
	public async Task PurgeFailingPastMaxAttemptsBecomesUnresolvedAndCompletesOnLaterReconciliation()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await StartAsync(
			postgres, ct,
			s =>
			{
				s["AgentOrchestration:Purge:MaxAttempts"] = "2";
				s["AgentOrchestration:Purge:RetryDelay"] = "00:00:01";
			});
		var (id, _, etag) = await AnsweredConversationAsync(f, ct);
		var problems = new List<Reply>(await DeleteAsync(f, id, etag, ct));

		await ExecuteAsync(
			f,
			"""
			CREATE FUNCTION socalytics.fail_agent_purge_test() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'purge-test-failure'; END $$;
			CREATE TRIGGER trg_zz_fail_agent_purge_test BEFORE DELETE ON socalytics.agent_accepted_advice
			    FOR EACH ROW EXECUTE FUNCTION socalytics.fail_agent_purge_test();
			""",
			ct);

		(await PurgeAsync(f, ct)).ShouldBe(1);
		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("pending/");
		(await PurgeStateAsync(f, id, "index", ct)).ShouldBe("completed/not-present");

		f.Factory.Time.Advance(TimeSpan.FromSeconds(2));
		(await PurgeAsync(f, ct)).ShouldBe(0);
		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("unresolved/");
		(await f.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'agent.conversation.purge-unresolved' AND resource_id = '{id}'", ct)).ShouldBe(1);

		// Unresolved purges are never member-facing.
		var read = await SendAsync(f.Admin, HttpMethod.Get, $"/api/v1/agent-conversations/{id}", null, null, ct);
		read.Status.ShouldBe(HttpStatusCode.NotFound, read.Text);
		problems.Add(read);
		var turns = await SendAsync(f.Admin, HttpMethod.Get, $"/api/v1/agent-conversations/{id}/turns", null, null, ct);
		turns.Status.ShouldBe(HttpStatusCode.NotFound, turns.Text);
		problems.Add(turns);

		await ExecuteAsync(
			f,
			"DROP TRIGGER trg_zz_fail_agent_purge_test ON socalytics.agent_accepted_advice; DROP FUNCTION socalytics.fail_agent_purge_test();",
			ct);
		await ReconcileAsync(f, ct);

		(await PurgeStateAsync(f, id, "primary", ct)).ShouldBe("completed/purged");
		(await CountAsync(f, "agent_accepted_advice", ct)).ShouldBe(0);
		(await CountAsync(f, "agent_lineage_entries", ct)).ShouldBe(0);
		await ShouldHoldNoTextAsync(f, problems.Select(p => p.Text), ct);
	}

	[Fact]
	public async Task PurgeOutboxMessageIsPublishedOnThePurgeSubject()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct,
			s =>
			{
				s["Analysis:Workers:Enabled"] = "true";
				s["ConnectionStrings:nats"] = nats.ConnectionString;
				s["Messaging:Nats:ProvisionTopology"] = "true";
			});
		var (id, _, etag) = await AnsweredConversationAsync(f, ct);
		await DeleteAsync(f, id, etag, ct);

		const string Where =
			"message_type = 'agent-conversation-purge' AND subject = 'agents.conversations.purge' AND state = 'published'";
		var published = false;
		for (var i = 0; i < 120 && !published; i++)
		{
			published = await f.ScalarAsync($"SELECT count(*) FROM socalytics.outbox_messages WHERE {Where}", ct) == 1;
			if (!published)
			{
				await Task.Delay(500, ct);
			}
		}

		published.ShouldBeTrue();
		var payload = JsonDocument.Parse(await TextAsync(
			f, "SELECT payload::text FROM socalytics.outbox_messages WHERE message_type = 'agent-conversation-purge'", ct)).RootElement;
		payload.GetProperty("conversationId").GetGuid().ShouldBe(id);
		payload.EnumerateObject().Select(p => p.Name).ShouldBe(["schemaVersion", "conversationId", "requestedAt"], ignoreOrder: true);
	}
}

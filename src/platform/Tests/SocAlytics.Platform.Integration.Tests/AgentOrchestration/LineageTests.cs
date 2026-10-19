using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class LineageTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";

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

	// The workers are off so that only the calls of the test move time-dependent state.
	private static void Manual(Dictionary<string, string?> s) => s["Analysis:Workers:Enabled"] = "false";

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<HttpResponseMessage> MemberAsync(
		AgentOrchestrationFixture f, HttpMethod method, string path, object? body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get)
		{
			request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
			request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		}

		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> ClaimAsync(AgentOrchestrationFixture f, string token, Guid invocationId, CancellationToken ct)
	{
		using var response = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
		return await ReadAsync(response, ct);
	}

	private static async Task CheckpointAsync(
		AgentOrchestrationFixture f, string token, JsonElement envelope, string note, CancellationToken ct)
	{
		var body = new
		{
			operationId = "op-" + Guid.NewGuid().ToString("N"),
			fencingToken = envelope.GetProperty("fencingToken").GetString(),
			kind = "progress",
			note,
		};
		using var response = await f.Workload.CheckpointAsync(f.Anon, f.Now, token, envelope.GetProperty("attemptId").GetGuid(), body, ct);
		response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
	}

	private static async Task AssertImmutableAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		var failure = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
		failure.MessageText.ShouldContain("immutable");
	}

	[Fact]
	public async Task LineageOfARetriedInvocationIsGapFreeImmutableAndConditionallyRead()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, Manual);
		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);
		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics },
			created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		using var submitted = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations",
			new { agentId = Tactics, question = "What should we change?" }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();

		var first = await ClaimAsync(f, token, invocationId, ct);
		await CheckpointAsync(f, token, first, "first attempt note", ct);
		var failure = new
		{
			operationId = "op-" + Guid.NewGuid().ToString("N"),
			fencingToken = first.GetProperty("fencingToken").GetString(),
			reasonCode = "model-error",
			retryable = true,
		};
		using (var failed = await f.Workload.FailAsync(f.Anon, f.Now, token, first.GetProperty("attemptId").GetGuid(), failure, ct))
		{
			failed.StatusCode.ShouldBe(HttpStatusCode.OK, await failed.Content.ReadAsStringAsync(ct));
			(await ReadAsync(failed, ct)).GetProperty("invocationState").GetString().ShouldBe("queued");
		}

		var second = await ClaimAsync(f, token, invocationId, ct);
		await CheckpointAsync(f, token, second, "second attempt note", ct);
		var completion = new
		{
			operationId = "op-" + Guid.NewGuid().ToString("N"),
			fencingToken = second.GetProperty("fencingToken").GetString(),
			advice = "Press higher.",
			executionReferences = References,
		};
		using (var completed = await f.Workload.CompleteAsync(f.Anon, f.Now, token, second.GetProperty("attemptId").GetGuid(), completion, ct))
		{
			completed.StatusCode.ShouldBe(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync(ct));
		}

		var path = $"/api/v1/agent-invocations/{invocationId}/lineage";
		using var read = await MemberAsync(f, HttpMethod.Get, path, null, null, ct);
		read.StatusCode.ShouldBe(HttpStatusCode.OK, await read.Content.ReadAsStringAsync(ct));
		var etag = read.Headers.ETag!.Tag;
		var lineage = await ReadAsync(read, ct);

		var entries = lineage.GetProperty("entries").EnumerateArray().ToList();
		entries.Select(e => e.GetProperty("kind").GetString()).ShouldBe(
			["submitted", "claimed", "checkpoint", "failed", "claimed", "checkpoint", "completed"]);
		entries.Select(e => e.GetProperty("sequence").GetInt32()).ShouldBe([1, 2, 3, 4, 5, 6, 7]);
		entries.Select(e => e.GetProperty("attemptNumber").ValueKind == JsonValueKind.Number
			? e.GetProperty("attemptNumber").GetInt32()
			: 0).ShouldBe([0, 1, 1, 1, 2, 2, 2]);
		entries[2].GetProperty("details").GetProperty("note").GetString().ShouldBe("first attempt note");
		entries[3].GetProperty("details").GetProperty("reasonCode").GetString().ShouldBe("model-error");
		entries[3].GetProperty("details").GetProperty("retryable").GetBoolean().ShouldBeTrue();
		entries[5].GetProperty("details").GetProperty("note").GetString().ShouldBe("second attempt note");
		entries[6].GetProperty("details").GetProperty("model").GetString().ShouldBe("test-model");

		var advice = lineage.GetProperty("advice");
		advice.GetProperty("advice").GetString().ShouldBe("Press higher.");
		advice.GetProperty("attemptId").GetGuid().ShouldBe(second.GetProperty("attemptId").GetGuid());
		var references = advice.GetProperty("executionReferences");
		foreach (var name in new[] { "runtime", "modelProvider", "model", "deployment", "promptRef", "toolPolicyRef", "safetyPolicyRef" })
		{
			references.GetProperty(name).GetString().ShouldNotBeNullOrEmpty();
		}

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_accepted_advice WHERE invocation_id = '{invocationId}'", ct)).ShouldBe(1);

		using (var unchanged = await ConditionalGetAsync(f, path, etag, ct))
		{
			unchanged.StatusCode.ShouldBe(HttpStatusCode.NotModified);
		}

		await AssertImmutableAsync(f, $"UPDATE socalytics.agent_lineage_entries SET kind = 'failed' WHERE invocation_id = '{invocationId}'", ct);
		await AssertImmutableAsync(f, $"DELETE FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}'", ct);
		await AssertImmutableAsync(f, $"UPDATE socalytics.agent_accepted_advice SET advice = 'changed' WHERE invocation_id = '{invocationId}'", ct);
		await AssertImmutableAsync(f, $"DELETE FROM socalytics.agent_accepted_advice WHERE invocation_id = '{invocationId}'", ct);

		(await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}'", ct)).ShouldBe(7);
	}

	private static async Task<HttpResponseMessage> ConditionalGetAsync(
		AgentOrchestrationFixture f, string path, string ifNoneMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
		return await f.Admin.Client.SendAsync(request, ct);
	}
}

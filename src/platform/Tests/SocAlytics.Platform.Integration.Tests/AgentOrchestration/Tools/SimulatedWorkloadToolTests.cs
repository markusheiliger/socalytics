using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.SimulatedAgentWorkload;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

// FR-019, FR-021; SC-006: the simulated workload's runner is hosted in process and drives the evidence tools through the real queue and tool operations.
public sealed class SimulatedWorkloadToolTests(PostgresContainerFixture postgres, NatsContainerFixture nats, RustFsContainerFixture rustFs)
{
	private const string Tactics = "specialist-tactics";

	private sealed record Outcome(JsonElement Invocation, List<JsonElement> Entries);

	private static async Task<HttpResponseMessage> MemberAsync(
		EvidenceScenario s, HttpMethod method, string path, object body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", s.Coach.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await s.Coach.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

	private async Task<Outcome> RunAsync(string question, bool restrictPolicy, CancellationToken ct)
	{
		using var workload = new TestAgentWorkload();
		var unique = Guid.NewGuid().ToString("N");
		var stream = $"AGENT_WORK_{unique}";
		var consumer = $"agent-workload-{unique}";
		await using var s = await EvidenceScenario.StartAsync(
			postgres,
			rustFs,
			ct,
			extraSettings: new Dictionary<string, string?>
			{
				["ConnectionStrings:nats"] = nats.ConnectionString,
				["Messaging:Nats:ProvisionTopology"] = "true",
				["Messaging:Nats:AgentWorkStream"] = stream,
				["Messaging:Nats:AgentWorkSubject"] = $"agents.invocations.ready.{unique}",
				["Messaging:Nats:AgentWorkConsumer"] = consumer,
				["Analysis:Workers:Enabled"] = "true",
				["AgentOrchestration:Execution:LeaseSeconds"] = "20",
				["AgentOrchestration:Execution:MaxAttemptSeconds"] = "60",
				["AgentOrchestration:Workloads:TokenLifetime"] = "00:05:00",
				["AgentOrchestration:Workloads:Clients:0:ClientId"] = workload.ClientId,
				["AgentOrchestration:Workloads:Clients:0:PublicJwk"] = workload.PublicJwk,
				["AgentOrchestration:Workloads:Clients:0:Enabled"] = "true",
			});

		// The segment worker materializes the demanded segments; the clip needs them available.
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (await s.ScalarAsync("SELECT count(*) FROM socalytics.materialized_segments WHERE state = 'available'", ct) < EvidenceScenario.SegmentCount
			&& DateTime.UtcNow < deadline)
		{
			await Task.Delay(200, ct);
		}

		await s.ScalarAsync($"WITH u AS (UPDATE socalytics.analysis_runs SET state = 'Completed', terminated_at = now() WHERE id = '{s.RunId}' RETURNING 1) SELECT count(*) FROM u", ct);
		using var created = await MemberAsync(
			s, HttpMethod.Post, $"/api/v1/matches/{s.MatchId}/agent-conversations", new { analysisRunId = s.RunId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			s, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics },
			created.Headers.ETag!.Tag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));
		using var submitted = await MemberAsync(
			s, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();

		if (restrictPolicy)
		{
			// Pins are immutable, so the test repoints this invocation's pin to a registered policy that omits get_player_stats.
			await using var pinConnection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
			await pinConnection.OpenAsync(ct);
			await pinConnection.ExecuteAsync(new CommandDefinition(
				$$"""
				INSERT INTO socalytics.agent_catalog_artifacts (kind, artifact_id, artifact_version, version_major, digest, agent_group, document, registered_by, registered_at)
				VALUES ('tool-policy', 'basic-tools', '0.1.0', 0, 'sha-256:{{new string('c', 64)}}', 'specialist',
				        '{"schemaVersion":"1.0.0","kind":"tool-policy","id":"basic-tools","version":"0.1.0","tools":["get_match","get_formations","get_video_clip"]}'::jsonb, 'test', now());
				ALTER TABLE socalytics.agent_invocation_pins DISABLE TRIGGER trg_agent_invocation_pins_immutable;
				UPDATE socalytics.agent_invocation_pins SET artifact_id = 'basic-tools', artifact_version = '0.1.0', digest = 'sha-256:{{new string('c', 64)}}'
				WHERE invocation_id = '{{invocationId}}' AND kind = 'tool-policy';
				ALTER TABLE socalytics.agent_invocation_pins ENABLE TRIGGER trg_agent_invocation_pins_immutable;
				""",
				cancellationToken: ct));
		}

		var options = new SimulatedAgentWorkloadOptions
		{
			ApiBaseAddress = TestAgentWorkload.Origin,
			ClientId = workload.ClientId,
			PrivateKey = workload.PrivateKey,
			NatsUrl = nats.ConnectionString,
			Stream = stream,
			Consumer = consumer,
			FetchExpiry = TimeSpan.FromSeconds(2),
			RetryDelay = TimeSpan.FromMilliseconds(200),
		};
		using var http = s.Factory.CreateApiClient();
		var log = new CapturingLoggerProvider();
		await using var runner = new SimulatedAgentWorkloadRunner(options, http, s.Factory.Time, log.CreateLogger("SimulatedAgentWorkload"));
		using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
		run.CancelAfter(TimeSpan.FromSeconds(60));
		var worker = runner.RunAsync(run.Token);

		// The invocation must succeed within 30 seconds of the run starting.
		var limit = DateTime.UtcNow.AddSeconds(30);
		JsonElement? ended = null;
		JsonElement? last = null;
		while (ended is null && DateTime.UtcNow < limit)
		{
			using var read = await s.Coach.Client.GetAsync($"/api/v1/agent-invocations/{invocationId}", ct);
			var body = await ReadAsync(read, ct);
			last = body;
			if (body.GetProperty("state").GetString() is "succeeded" or "failed")
			{
				ended = body;
				break;
			}

			await Task.Delay(250, ct);
		}

		await run.CancelAsync();
		await worker;
		ended.ShouldNotBeNull($"The invocation did not end in time. Last read: {last?.GetRawText() ?? "none"}");

		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		var payloads = await connection.QueryAsync<string>(new CommandDefinition(
			$"SELECT payload::text FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'tool-call' ORDER BY sequence",
			cancellationToken: ct));
		return new Outcome(ended.Value, [.. payloads.Select(p => JsonDocument.Parse(p).RootElement.Clone())]);
	}

	[Fact]
	public async Task Facts_and_clip_markers_succeed_with_the_tool_entries_in_lineage_and_simulated_advice()
	{
		var ct = TestContext.Current.CancellationToken;
		var outcome = await RunAsync("[simulate:tools-facts] [simulate:tools-clip] Which pressing triggers did the team miss?", false, ct);

		outcome.Invocation.GetProperty("state").GetString().ShouldBe("succeeded");
		var tools = outcome.Entries.Select(e => e.GetProperty("toolId").GetString()).ToList();
		tools.Count(t => t == "get_match").ShouldBe(2);
		tools.ShouldContain("get_video_clip");
		tools.ShouldContain("get_formations");
		outcome.Entries.First(e => e.GetProperty("toolId").GetString() == "get_video_clip").GetProperty("outcome").GetString().ShouldBe("answered");
		outcome.Entries.First(e => e.GetProperty("toolId").GetString() == "get_formations").GetProperty("outcome").GetString().ShouldBe("unavailable");

		var advice = outcome.Invocation.GetProperty("advice").GetString();
		advice.ShouldNotBeNull();
		advice.ShouldContain("Simulated tool outcomes:");
		advice.ShouldContain("get_video_clip=answered");
		advice.ShouldContain("get_formations=unavailable");
	}

	[Fact]
	public async Task The_denied_marker_records_a_not_permitted_entry_and_the_invocation_still_succeeds()
	{
		var ct = TestContext.Current.CancellationToken;
		var outcome = await RunAsync("[simulate:tools-denied] Which pressing triggers did the team miss?", true, ct);

		outcome.Invocation.GetProperty("state").GetString().ShouldBe("succeeded");
		outcome.Entries.ShouldContain(e => e.GetProperty("outcome").GetString() == "not-permitted");
		outcome.Invocation.GetProperty("advice").GetString()!.ShouldContain("get_player_stats=not-permitted");
	}
}

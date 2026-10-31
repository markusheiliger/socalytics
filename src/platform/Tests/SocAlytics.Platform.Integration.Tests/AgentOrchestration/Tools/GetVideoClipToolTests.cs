using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Analysis.Evidence.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Segments;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

// SC-001, SC-004; FR-012, FR-013, FR-017, FR-018a over HTTP against PostgreSQL, RustFS, and the pinned FFmpeg toolchain.
public sealed class GetVideoClipToolTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs)
{
	private const string Tactics = "specialist-tactics";
	private const string PathSegment = "get-video-clip";
	private const string GrantedEventType = "agent.tool.evidence-media.granted";

	private sealed class SwitchableStorage(IObjectStorage inner) : DelegatingStorage(inner)
	{
		public volatile bool ChecksumUnavailable;

		public override Task<ObjectChecksum?> GetObjectChecksumAsync(string objectKey, CancellationToken cancellationToken) =>
			ChecksumUnavailable ? throw new ObjectStorageUnavailableException() : base.GetObjectChecksumAsync(objectKey, cancellationToken);
	}

	private sealed record Attempt(TestAgentWorkload Workload, HttpClient Client, string Token, Guid InvocationId, Guid AttemptId, string FencingToken);

	private async Task<EvidenceScenario> StartAsync(TestAgentWorkload workload, CancellationToken ct) =>
		await EvidenceScenario.StartAsync(
			postgres,
			rustFs,
			ct,
			extraSettings: new Dictionary<string, string?>
			{
				["Analysis:Evidence:MaxDuration"] = "00:00:03",
				["AgentOrchestration:Workloads:TokenLifetime"] = "00:05:00",
				["AgentOrchestration:Workloads:Clients:0:ClientId"] = workload.ClientId,
				["AgentOrchestration:Workloads:Clients:0:PublicJwk"] = workload.PublicJwk,
				["AgentOrchestration:Workloads:Clients:0:Enabled"] = "true",
			},
			configureServices: services =>
			{
				var descriptor = services.Last(d => d.ServiceType == typeof(IObjectStorage));
				services.Remove(descriptor);
				services.AddSingleton<IObjectStorage>(sp =>
					new SwitchableStorage((IObjectStorage)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!)));
			});

	private static SwitchableStorage Storage(EvidenceScenario s) => (SwitchableStorage)s.Factory.Services.GetRequiredService<IObjectStorage>();

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

	// Binds an invocation to the scenario's run (made terminal and successful) and claims it as the workload.
	private static async Task<Attempt> ClaimAttemptAsync(EvidenceScenario s, TestAgentWorkload workload, CancellationToken ct)
	{
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
			s, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations",
			new { agentId = Tactics, question = "Show the clip." }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();

		var client = s.Factory.CreateApiClient();
		var now = s.Factory.Time.GetUtcNow();
		var token = await workload.AccessTokenAsync(client, now, ct);
		using var claim = await workload.ClaimAsync(client, now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
		var envelope = await ReadAsync(claim, ct);
		return new Attempt(
			workload, client, token, invocationId, envelope.GetProperty("attemptId").GetGuid(),
			envelope.GetProperty("fencingToken").GetString()!);
	}

	private static Task<HttpResponseMessage> CallAsync(EvidenceScenario s, Attempt a, object arguments, CancellationToken ct) =>
		a.Workload.CallToolAsync(
			a.Client, s.Factory.Time.GetUtcNow(), a.Token, a.AttemptId, PathSegment, a.FencingToken,
			"call-" + Guid.NewGuid().ToString("N"), arguments, ct);

	private static async Task<(JsonElement Answer, JsonElement Member)> BothAsync(
		EvidenceScenario s, Attempt a, object body, CancellationToken ct)
	{
		using var member = await s.Coach.SendAsync(HttpMethod.Post, s.EvidencePath, body, ct);
		member.StatusCode.ShouldBe(HttpStatusCode.OK, await member.Content.ReadAsStringAsync(ct));
		using var tool = await CallAsync(s, a, body, ct);
		var text = await tool.Content.ReadAsStringAsync(ct);
		tool.StatusCode.ShouldBe(HttpStatusCode.OK, text);
		var answer = JsonDocument.Parse(text).RootElement.Clone();
		answer.GetProperty("toolId").GetString().ShouldBe("get_video_clip");
		answer.GetProperty("outcome").GetString().ShouldBe("answered");
		return (answer.GetProperty("result"), await ReadAsync(member, ct));
	}

	// The comparison ignores only the per-request link and its expiry.
	private static string Comparable(JsonElement resolution)
	{
		var node = JsonNode.Parse(resolution.GetRawText())!.AsObject();
		foreach (var part in node["parts"]!.AsArray())
		{
			if (part!["media"] is JsonObject media)
			{
				media.Remove("url");
				media.Remove("expiresAt");
			}
		}

		var result = new JsonObject();
		foreach (var key in new[] { "requested", "fact", "shortened", "parts" })
		{
			if (node.TryGetPropertyValue(key, out var value))
			{
				result[key] = value?.DeepClone();
			}
		}

		return result.ToJsonString();
	}

	private static IEnumerable<string> Urls(JsonElement resolution) =>
		resolution.GetProperty("parts").EnumerateArray()
			.Where(p => p.TryGetProperty("media", out _))
			.Select(p => p.GetProperty("media").GetProperty("url").GetString()!);

	private static async Task<List<string>> QueryAsync(EvidenceScenario s, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(s.Setup.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		return [.. await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct))];
	}

	[Fact]
	public async Task Fact_and_range_resolutions_equal_the_member_resolution_and_issued_links_are_never_recorded()
	{
		var ct = TestContext.Current.CancellationToken;
		using var workload = new TestAgentWorkload();
		await using var s = await StartAsync(workload, ct);
		await s.MaterializeAllAsync(ct);
		var attempt = await ClaimAttemptAsync(s, workload, ct);
		var urls = new List<string>();

		var inside = EvidenceScenario.InsideFactOrdinal;
		var boundary = EvidenceScenario.BoundaryFactOrdinal;
		// The last fact window exceeds the 3-second maximum, so its answer is shortened.
		object[] cases =
		[
			new { fact = new { acceptedResultId = s.ResultId, ordinal = inside } },
			new { fact = new { acceptedResultId = s.ResultId, ordinal = boundary, contextBeforeMs = 0, contextAfterMs = 0 } },
			new { fact = new { acceptedResultId = s.ResultId, ordinal = boundary, contextBeforeMs = 1_000, contextAfterMs = 1_000 } },
			new { range = new { fromMs = 3_500, toMs = 6_000 } },
		];

		var shortened = 0;
		foreach (var body in cases)
		{
			var (answer, member) = await BothAsync(s, attempt, body, ct);
			Comparable(answer).ShouldBe(Comparable(member));
			answer.GetProperty("parts").EnumerateArray().ShouldAllBe(p => p.GetProperty("state").GetString() == "available");
			urls.AddRange(Urls(answer));
			if (answer.GetProperty("shortened").GetBoolean())
			{
				shortened++;
			}
		}

		shortened.ShouldBeGreaterThanOrEqualTo(1);
		urls.Count.ShouldBeGreaterThan(0);

		var grants = await QueryAsync(
			s, $"SELECT details::text FROM socalytics.security_audit_event WHERE event_type = '{GrantedEventType}' ORDER BY occurred_at", ct);
		grants.Count.ShouldBe(cases.Length);
		using (var details = JsonDocument.Parse(grants[0]))
		{
			var root = details.RootElement;
			root.GetProperty("attemptId").GetString().ShouldBe(attempt.AttemptId.ToString());
			root.GetProperty("invocationId").GetString().ShouldBe(attempt.InvocationId.ToString());
			root.GetProperty("tool").GetString().ShouldBe("get_video_clip");
			root.GetProperty("mediaDigests").GetString()!.ShouldStartWith("sha-256:");
			DateTimeOffset.Parse(root.GetProperty("grantExpiresAt").GetString()!).ShouldBeGreaterThan(s.Factory.Time.GetUtcNow());
		}

		var texts = new List<string>();
		foreach (var table in new[] { "security_audit_event", "agent_tool_calls", "agent_lineage_entries", "outbox_messages", "materialized_segments" })
		{
			texts.AddRange(await QueryAsync(s, $"SELECT to_jsonb(t)::text FROM socalytics.{table} t", ct));
		}

		texts.AddRange(s.Logs.Entries.SelectMany(e => new[] { e.Message, e.Exception ?? string.Empty }
			.Concat(e.State.Select(p => $"{p.Key}={p.Value}"))));
		texts.ShouldNotContain(t => t.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase));
		foreach (var url in urls.Distinct())
		{
			texts.ShouldNotContain(t => t.Contains(url, StringComparison.Ordinal));
		}

		var lineage = await QueryAsync(
			s, "SELECT payload::text FROM socalytics.agent_lineage_entries WHERE kind = 'tool-call' ORDER BY sequence", ct);
		lineage.Count.ShouldBe(cases.Length);
		lineage.ShouldAllBe(p => p.Contains("\"answered\"", StringComparison.Ordinal) && p.Contains("sha-256:", StringComparison.Ordinal));
	}

	[Fact]
	public async Task A_fact_outside_the_bound_run_answers_not_found()
	{
		var ct = TestContext.Current.CancellationToken;
		using var workload = new TestAgentWorkload();
		await using var s = await StartAsync(workload, ct);
		var attempt = await ClaimAttemptAsync(s, workload, ct);

		foreach (var fact in new object[]
		{
			new { acceptedResultId = Guid.NewGuid(), ordinal = 0 },
			new { acceptedResultId = s.ResultId, ordinal = 99 },
		})
		{
			using var response = await CallAsync(s, attempt, new { fact }, ct);
			response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync(ct));
		}

		(await s.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{GrantedEventType}'", ct)).ShouldBe(0);
		var lineage = await QueryAsync(
			s, "SELECT payload::text FROM socalytics.agent_lineage_entries WHERE kind = 'tool-call' ORDER BY sequence", ct);
		lineage.Count.ShouldBe(2);
		lineage.ShouldAllBe(p => p.Contains("\"not-found\"", StringComparison.Ordinal));
	}

	[Fact]
	public async Task Storage_failure_answers_503_with_lineage_outcome_failed_and_no_links()
	{
		var ct = TestContext.Current.CancellationToken;
		using var workload = new TestAgentWorkload();
		await using var s = await StartAsync(workload, ct);
		await s.MaterializeAllAsync(ct);
		var attempt = await ClaimAttemptAsync(s, workload, ct);

		Storage(s).ChecksumUnavailable = true;
		using var response = await CallAsync(
			s, attempt, new { fact = new { acceptedResultId = s.ResultId, ordinal = EvidenceScenario.InsideFactOrdinal } }, ct);

		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable, text);
		JsonDocument.Parse(text).RootElement.GetProperty("code").GetString().ShouldBe("evidence-storage-unavailable");
		text.ShouldNotContain("X-Amz-Signature");

		var lineage = await QueryAsync(
			s, "SELECT payload::text FROM socalytics.agent_lineage_entries WHERE kind = 'tool-call' ORDER BY sequence", ct);
		using var payload = JsonDocument.Parse(lineage.ShouldHaveSingleItem());
		payload.RootElement.GetProperty("outcome").GetString().ShouldBe("failed");
		payload.RootElement.GetProperty("reason").GetString().ShouldBe("evidence-storage-unavailable");
		(await s.ScalarAsync($"SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = '{GrantedEventType}'", ct)).ShouldBe(0);
	}
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Tools;

// FR-003, FR-014; SC-005: the nine tools without an accepted producer answer unavailable even when the bound run has indexed facts.
public sealed class UnavailableAnalysisToolTests(PostgresContainerFixture postgres)
{
	private const string Tactics = "specialist-tactics";
	private static readonly string DigestA = "sha-256:" + new string('a', 64);
	private static readonly string DigestB = "sha-256:" + new string('b', 64);

	public static TheoryData<string, string, string?> Tools => new()
	{
		{ "get_player_stats", "get-player-stats", null },
		{ "find_counter_attacks", "find-counter-attacks", "counter-attack-analysis" },
		{ "find_turnovers", "find-turnovers", "possession-and-transitions" },
		{ "get_heatmap", "get-heatmap", "spatial-aggregation" },
		{ "find_ball_actions", "find-ball-actions", "ball-action-analysis" },
		{ "find_set_plays", "find-set-plays", "set-play-analysis" },
		{ "get_formations", "get-formations", "formation-analysis" },
		{ "find_pressing_phases", "find-pressing-phases", "pressing-analysis" },
		{ "get_tactical_phases", "get-tactical-phases", "tactical-phase-aggregation" },
	};

	private sealed record Attempt(Guid AttemptId, Guid InvocationId, string FencingToken, string Token);

	private static string NodeSnapshot() =>
		$$"""
		{"nodeKey":"detect","capabilityId":"cap.detect","schemaVersion":"1.0.0","analystProfileId":"profile-detect",
		 "analystProfileDigest":"{{DigestA}}","imageDigest":"{{DigestB}}","modelDigest":"{{DigestA}}"}
		""";

	// A completed run whose single accepted result has indexed facts.
	private static async Task<Guid> SeedRunWithFactsAsync(AgentOrchestrationFixture f, CancellationToken ct)
	{
		var detect = Guid.CreateVersion7();
		var run = new NewAnalysisRun(
			f.Hierarchy.TeamId, f.Hierarchy.MatchId, Guid.NewGuid(), DateTimeOffset.UtcNow, "wf", "1", ["cap.detect"], "all-requested-capabilities",
			AnalysisRunState.Completed, null,
			new AnalysisRunSnapshotRecord("""{"segmentationPolicy":{"version":"1","durationSeconds":300,"digest":"x"}}""", DigestA),
			[new WorkflowNodeRecord(detect, "detect", "cap.detect", "1.0.0", CapabilityTier.LowLevel, ExecutionScope.Segment, 0, WorkflowNodeState.Completed, null, default, NodeSnapshot())],
			[]);
		var snapshot = new LogicalJobSnapshotRecord(3, 60, 120, 600, DigestB, DigestA, null, "{}", "1.0.0");
		var job = new NewLogicalJob(Guid.CreateVersion7(), default, detect, ExecutionScope.Segment, Guid.NewGuid(), DigestA, DigestB, 1, LogicalJobState.Waiting, snapshot);

		Guid runId;
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			runId = await scope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().InsertAsync(run, ct);
			await scope.ServiceProvider.GetRequiredService<ILogicalJobStore>().InsertAsync([job with { RunId = runId }], ct);
			await uow.CommitAsync(ct);
		}

		var occurrence = Guid.NewGuid();
		var attempt = Guid.NewGuid();
		await using (var connection = new NpgsqlConnection(f.Db.AppConnectionString))
		{
			await connection.OpenAsync(ct);
			await connection.ExecuteAsync(new CommandDefinition(
				"INSERT INTO socalytics.analysis_readiness_occurrences (id, logical_job_id, sequence, cause, created_at) VALUES (@occurrence, @jobId, 1, 'initial', now())",
				new { occurrence, jobId = job.Id }, cancellationToken: ct));
			await connection.ExecuteAsync(new CommandDefinition(
				"""
				INSERT INTO socalytics.analysis_execution_attempts
					(id, logical_job_id, readiness_occurrence_id, attempt_number, manager_id, fencing_token, state, claimed_at, lease_expires_at, last_heartbeat_at)
				VALUES (@attempt, @jobId, @occurrence, 1, gen_random_uuid(), nextval('socalytics.analysis_fencing_token_seq'), 'Stale', now(), now(), now())
				""",
				new { attempt, jobId = job.Id, occurrence }, cancellationToken: ct));
		}

		var result = new AcceptedResultRecord(
			Guid.NewGuid(), job.Id, runId, detect, attempt, "results/key", DigestA, "cap.detect", "1.0.0", "1.0.0",
			DigestB, DigestA, null, "{}", "{}", [], DateTimeOffset.UtcNow);
		var facts = Enumerable.Range(0, 3).Select(o => new IndexedFactRecord(
			result.Id, o, runId, detect, f.Hierarchy.TeamId, f.Hierarchy.MatchId, "cap.detect", "1.0.0", "1.0.0",
			"foundational-facts", "pass", o * 100, o * 100 + 100, 0.5, $$"""{"o":{{o}}}""")).ToList();
		await using (var scope = f.Factory.Services.CreateAsyncScope())
		{
			await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
			await scope.ServiceProvider.GetRequiredService<IAcceptedResultStore>().InsertAsync(result, ct);
			await scope.ServiceProvider.GetRequiredService<IIndexedFactStore>().InsertAsync(facts, ct);
			await uow.CommitAsync(ct);
		}

		return runId;
	}

	private static async Task<HttpResponseMessage> MemberPostAsync(
		AgentOrchestrationFixture f, string path, object body, string? ifMatch, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
		request.Headers.Add("X-CSRF-Token", f.Admin.AntiforgeryToken);
		request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
		if (ifMatch is not null)
		{
			request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
		}

		return await f.Admin.Client.SendAsync(request, ct);
	}

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct, HttpStatusCode expected = HttpStatusCode.OK)
	{
		var text = await response.Content.ReadAsStringAsync(ct);
		response.StatusCode.ShouldBe(expected, text);
		return JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone();
	}

	private static async Task<Attempt> StartAttemptAsync(AgentOrchestrationFixture f, Guid runId, CancellationToken ct)
	{
		using var created = await MemberPostAsync(
			f, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		var conversationId = (await ReadAsync(created, ct, HttpStatusCode.Created)).GetProperty("conversationId").GetGuid();
		using var added = await MemberPostAsync(
			f, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, created.Headers.ETag!.Tag, ct);
		await ReadAsync(added, ct);
		using var submitted = await MemberPostAsync(
			f, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = "q" }, null, ct);
		var invocationId = (await ReadAsync(submitted, ct, HttpStatusCode.Accepted)).GetProperty("invocationId").GetGuid();

		var token = await f.Workload.AccessTokenAsync(f.Anon, f.Now, ct);
		using var claim = await f.Workload.ClaimAsync(f.Anon, f.Now, token, invocationId, "claim-" + Guid.NewGuid().ToString("N"), ct);
		var envelope = await ReadAsync(claim, ct, HttpStatusCode.Created);
		return new Attempt(envelope.GetProperty("attemptId").GetGuid(), invocationId, envelope.GetProperty("fencingToken").GetString()!, token);
	}

	[Theory]
	[MemberData(nameof(Tools))]
	public async Task A_tool_without_an_accepted_producer_answers_unavailable_and_writes_a_lineage_entry(
		string toolId, string pathSegment, string? producer)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var runId = await SeedRunWithFactsAsync(f, ct);
		var attempt = await StartAttemptAsync(f, runId, ct);

		var callId = "call-" + Guid.NewGuid().ToString("N");
		using var response = await f.Workload.CallToolAsync(
			f.Anon, f.Now, attempt.Token, attempt.AttemptId, pathSegment, attempt.FencingToken, callId, new { fromMs = 0, toMs = 1000 }, ct);
		var answer = await ReadAsync(response, ct);
		answer.GetProperty("callId").GetString().ShouldBe(callId);
		answer.GetProperty("toolId").GetString().ShouldBe(toolId);
		answer.GetProperty("outcome").GetString().ShouldBe("unavailable");

		var result = answer.GetProperty("result");
		result.GetProperty("reason").GetString().ShouldBe("no-accepted-producer");
		if (producer is null)
		{
			result.GetProperty("producingCapability").ValueKind.ShouldBe(JsonValueKind.Null);
		}
		else
		{
			result.GetProperty("producingCapability").GetString().ShouldBe(producer);
		}

		// No fact content reaches the answer although the run has indexed facts.
		result.TryGetProperty("items", out _).ShouldBeFalse();
		result.TryGetProperty("facts", out _).ShouldBeFalse();
		result.GetRawText().ShouldNotContain("foundational-facts");

		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		var lineage = (await connection.QueryAsync<string>(new CommandDefinition(
			$"SELECT payload::text FROM socalytics.agent_lineage_entries WHERE invocation_id = '{attempt.InvocationId}' AND kind = 'tool-call'",
			cancellationToken: ct))).ShouldHaveSingleItem();
		using var payload = JsonDocument.Parse(lineage);
		var entry = payload.RootElement;
		entry.GetProperty("callId").GetString().ShouldBe(callId);
		entry.GetProperty("toolId").GetString().ShouldBe(toolId);
		entry.GetProperty("outcome").GetString().ShouldBe("unavailable");
		entry.GetProperty("reason").GetString().ShouldBe("no-accepted-producer");
		entry.GetProperty("evidence").GetProperty("facts").GetArrayLength().ShouldBe(0);
		entry.GetProperty("evidence").GetProperty("media").GetArrayLength().ShouldBe(0);
		if (producer is null)
		{
			(!entry.TryGetProperty("producingCapability", out var value) || value.ValueKind == JsonValueKind.Null).ShouldBeTrue();
		}
		else
		{
			entry.GetProperty("producingCapability").GetString().ShouldBe(producer);
		}
	}
}

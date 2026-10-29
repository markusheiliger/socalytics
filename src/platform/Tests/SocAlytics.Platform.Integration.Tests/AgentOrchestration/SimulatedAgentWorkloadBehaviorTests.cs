using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.SimulatedAgentWorkload;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

// The in-process runner drives each question marker through the real queue, claim, and report operations (lease 2 s, 3 attempts).
public sealed class SimulatedAgentWorkloadBehaviorTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
	private const string Tactics = "specialist-tactics";

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

	private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
		JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

	private static async Task<Guid> SubmitAsync(AgentOrchestrationFixture f, string question, CancellationToken ct)
	{
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
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		return (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();
	}

	// Runs the simulated workload until the invocation is terminal; a stalled first attempt is expired by moving the clock past its lease.
	private async Task<(JsonElement Invocation, long Attempts, long Checkpoints)> RunToEndAsync(
		string marker, bool expireFirstAttempt, CancellationToken ct)
	{
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct,
			s =>
			{
				s["ConnectionStrings:nats"] = nats.ConnectionString;
				s["Messaging:Nats:ProvisionTopology"] = "true";
				s["Analysis:Workers:Enabled"] = "true";
			});
		var invocationId = await SubmitAsync(f, $"{marker} Which pressing triggers did the team miss?", ct);

		var options = new SimulatedAgentWorkloadOptions
		{
			ApiBaseAddress = TestAgentWorkload.Origin,
			ClientId = f.Workload.ClientId,
			PrivateKey = f.Workload.PrivateKey,
			NatsUrl = nats.ConnectionString,
			Stream = f.AgentWorkStream,
			Consumer = f.AgentWorkConsumer,
			FetchExpiry = TimeSpan.FromSeconds(2),
			RetryDelay = TimeSpan.FromMilliseconds(200),
		};
		using var http = f.Factory.CreateApiClient();
		var runnerLog = new CapturingLoggerProvider();
		await using var runner = new SimulatedAgentWorkloadRunner(options, http, f.Factory.Time, runnerLog.CreateLogger("SimulatedAgentWorkload"));
		using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
		// One limit bounds the wait and the runner, so the runner is never stopped while the wait still runs.
		var limit = TimeSpan.FromMinutes(3);
		run.CancelAfter(limit + TimeSpan.FromSeconds(10));
		using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
		wait.CancelAfter(limit);
		var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.AttemptStalled += _ => stalled.TrySetResult();
		var worker = runner.RunAsync(run.Token);

		// Cause (research.md candidates 1 and 3, established from the code and not reproduced as a failure): the claim commits the active
		// attempt row before the runner has read the claim response, so a blind 3 s clock jump after "one attempt row" can land before the
		// stall is in effect (candidate 1) or before the first lease is established or past the second attempt's lease (candidate 3).
		// Probe runs with the old blind step restored: 0 of 10 failed with a 1.5 s pause between the claim response and the stall branch
		// of the runner, and 0 of 10 failed without it, so the failure rate is below what 10 runs can show and the cause stays a code-level
		// race. Candidates 2, 4, and 5 are excluded only by absence: none of the 20 probe runs showed a rejected DPoP proof, an out-of-order
		// or duplicate claim, or any other failure, so no log pointed at them. The cause lives in this helper; a runner-side stall event
		// is required (a), because the active row exists before the runner stalls; the platform is not defective (b), so it is unchanged.
		// The stall is therefore observed on the runner, and the clock moves once, just past the first attempt's recorded lease expiry.
		if (expireFirstAttempt)
		{
			try
			{
				await stalled.Task.WaitAsync(wait.Token);
				var leaseExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(await f.ScalarAsync(
					$"SELECT (extract(epoch FROM lease_expires_at) * 1000)::bigint FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}' AND attempt_number = 1", ct));
				f.Factory.Time.Advance(leaseExpiresAt - f.Factory.Time.GetUtcNow() + TimeSpan.FromMilliseconds(100));
				var expiredSql = $"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}' AND state = 'expired' AND end_reason = 'lease-expired'";
				while (await f.ScalarAsync(expiredSql, ct) == 0)
				{
					await Task.Delay(50, wait.Token);
				}

				(await f.ScalarAsync(expiredSql, ct)).ShouldBe(1);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
			}
		}

		JsonElement? ended = null;
		JsonElement? lastRead = null;
		while (ended is null && !wait.IsCancellationRequested)
		{
			using var read = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, ct);
			var body = await ReadAsync(read, ct);
			lastRead = body;
			if (body.GetProperty("state").GetString() is "succeeded" or "failed")
			{
				ended = body;
				break;
			}

			try
			{
				await Task.Delay(250, wait.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
			}
		}

		await run.CancelAsync();
		await worker;

		var attempts = await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}'", ct);
		var checkpoints = await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'checkpoint'", ct);
		var runnerLines = string.Join(" | ", runnerLog.Entries.Select(e => $"{e.Level} {e.Message}{(e.Exception is null ? "" : " " + e.Exception)}"));
		ended.ShouldNotBeNull(
			$"The invocation did not end in time. Last read: {lastRead?.GetRawText() ?? "none"}; attempts: {attempts}; checkpoints: {checkpoints}; runner log: {runnerLines}");
		return (ended.Value, attempts, checkpoints);
	}

	[Fact]
	public async Task FailOnceSucceedsOnTheSecondAttempt()
	{
		var (invocation, attempts, checkpoints) = await RunToEndAsync("[simulate:fail-once]", false, TestContext.Current.CancellationToken);

		invocation.GetProperty("state").GetString().ShouldBe("succeeded");
		attempts.ShouldBe(2);
		checkpoints.ShouldBeGreaterThanOrEqualTo(1);
	}

	[Fact]
	public async Task FailRetryableEndsWithAttemptsExhausted()
	{
		var (invocation, attempts, _) = await RunToEndAsync("[simulate:fail-retryable]", false, TestContext.Current.CancellationToken);

		invocation.GetProperty("state").GetString().ShouldBe("failed");
		invocation.GetProperty("failureReason").GetString().ShouldBe("attempts-exhausted");
		attempts.ShouldBe(3);
	}

	[Fact]
	public async Task FailPermanentEndsWithExecutionFailed()
	{
		var (invocation, attempts, _) = await RunToEndAsync("[simulate:fail-permanent]", false, TestContext.Current.CancellationToken);

		invocation.GetProperty("state").GetString().ShouldBe("failed");
		invocation.GetProperty("failureReason").GetString().ShouldBe("execution-failed");
		attempts.ShouldBe(1);
	}

	[Fact]
	public async Task StallExpiresTheFirstAttemptAndSucceedsOnTheSecond()
	{
		var (invocation, attempts, checkpoints) = await RunToEndAsync("[simulate:stall]", true, TestContext.Current.CancellationToken);

		invocation.GetProperty("state").GetString().ShouldBe("succeeded");
		attempts.ShouldBe(2);
		checkpoints.ShouldBeGreaterThanOrEqualTo(1);
	}
}

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
			FetchExpiry = TimeSpan.FromSeconds(2),
			RetryDelay = TimeSpan.FromMilliseconds(200),
		};
		using var http = f.Factory.CreateApiClient();
		await using var runner = new SimulatedAgentWorkloadRunner(options, http, f.Factory.Time);
		using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
		run.CancelAfter(TimeSpan.FromSeconds(60));
		var worker = runner.RunAsync(run.Token);

		var expired = false;
		JsonElement? ended = null;
		var deadline = DateTime.UtcNow.AddSeconds(60);
		while (ended is null && DateTime.UtcNow < deadline)
		{
			using var read = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, ct);
			var body = await ReadAsync(read, ct);
			if (body.GetProperty("state").GetString() is "succeeded" or "failed")
			{
				ended = body;
				break;
			}

			if (expireFirstAttempt && !expired
				&& await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}'", ct) == 1)
			{
				f.Factory.Time.Advance(TimeSpan.FromSeconds(3));
				expired = true;
			}

			await Task.Delay(250, ct);
		}

		await run.CancelAsync();
		await worker;

		ended.ShouldNotBeNull("The invocation did not end in time.");
		var attempts = await f.ScalarAsync($"SELECT count(*) FROM socalytics.agent_attempts WHERE invocation_id = '{invocationId}'", ct);
		var checkpoints = await f.ScalarAsync(
			$"SELECT count(*) FROM socalytics.agent_lineage_entries WHERE invocation_id = '{invocationId}' AND kind = 'checkpoint'", ct);
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

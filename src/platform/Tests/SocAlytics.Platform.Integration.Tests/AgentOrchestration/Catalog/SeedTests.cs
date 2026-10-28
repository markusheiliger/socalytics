using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Infrastructure.AgentOrchestration.Catalog;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog.Support;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.SimulatedAgentWorkload;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration.Catalog;

// FR-026, SC-001: an empty catalog is seeded once in Development and Test with the two approved and enabled baseline specialists.
public sealed class SeedTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
{
	private const string Tactics = "specialist-tactics";
	private const string Video = "specialist-video-analysis";
	private const string SeedVersion = "0.1.0";

	private static readonly string[] Specialists = [Tactics, Video];

	private static void SeedOff(Dictionary<string, string?> s) => s["AgentOrchestration:Catalog:SeedEnabled"] = "false";

	private sealed class FakeEnvironment(string name) : IHostEnvironment
	{
		public string EnvironmentName { get; set; } = name;

		public string ApplicationName { get; set; } = "SeedTests";

		public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

		public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
	}

	// A seeder as the host would run it, with the default (empty) configuration so that the seed switch is on.
	private static async Task RunSeederAsync(AgentOrchestrationFixture f, string environment, CancellationToken ct)
	{
		var services = f.Factory.Services;
		var seeder = new AgentCatalogSeeder(
			services.GetRequiredService<IServiceScopeFactory>(),
			new FakeEnvironment(environment),
			new ConfigurationBuilder().Build(),
			services.GetRequiredService<HealthCheckService>(),
			f.Factory.Time,
			NullLogger<AgentCatalogSeeder>.Instance);
		await seeder.StartAsync(ct);
		await seeder.StopAsync(ct);
	}

	private static async Task<List<string?[]>> RowsAsync(AgentOrchestrationFixture f, string sql, CancellationToken ct)
	{
		await using var connection = new NpgsqlConnection(f.Db.MigratorConnectionString);
		await connection.OpenAsync(ct);
		await using var command = new NpgsqlCommand(sql, connection);
		await using var reader = await command.ExecuteReaderAsync(ct);
		var rows = new List<string?[]>();
		while (await reader.ReadAsync(ct))
		{
			rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i)))]);
		}

		return rows;
	}

	private static string RepositoryFile(params string[] relative)
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine([dir.FullName, .. relative])))
		{
			dir = dir.Parent;
		}

		return Path.Combine([dir?.FullName ?? throw new FileNotFoundException(Path.Combine(relative)), .. relative]);
	}

	// The fenced text block below the heading of a First-Draft System Prompt.
	private static string FirstDraftPrompt(string agentId)
	{
		var lines = File.ReadAllLines(RepositoryFile("docs", "architecture", "intelligence-agent-catalog.md"));
		var heading = Array.IndexOf(lines, $"### Prompt Draft: `{agentId}`");
		heading.ShouldBeGreaterThan(-1);
		var start = Array.FindIndex(lines, heading, l => l == "```text") + 1;
		var end = Array.FindIndex(lines, start, l => l == "```");
		return string.Join("\n", lines[start..end]);
	}

	private static async Task<long> CountAsync(AgentOrchestrationFixture f, string table, CancellationToken ct) =>
		await f.ScalarAsync($"SELECT count(*) FROM socalytics.{table}", ct);

	[Fact]
	public async Task AnEmptyCatalogIsSeededWithTheTwoApprovedAndEnabledBaselineSpecialists()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);

		var versions = await RowsAsync(
			f, "SELECT agent_id, agent_version, state, approved_by, registered_by, agent_group FROM socalytics.agent_versions ORDER BY agent_id", ct);
		versions.Select(v => v[0]).ShouldBe([Tactics, Video]);
		foreach (var version in versions)
		{
			version[1].ShouldBe(SeedVersion);
			version[2].ShouldBe("approved");
			version[3].ShouldBe("system:seed");
			version[4].ShouldBe("system:seed");
			version[5].ShouldBe("specialist");
		}

		var enablements = await RowsAsync(f, "SELECT agent_id, agent_version FROM socalytics.agent_enablements ORDER BY agent_id", ct);
		enablements.Where(e => e[1] is not null).Select(e => (e[0], e[1])).Order()
			.ShouldBe([(Tactics, SeedVersion), (Video, SeedVersion)]);
		enablements.Count(e => e[1] is null).ShouldBe(enablements.Count - 2);

		foreach (var agent in Specialists)
		{
			var prompt = await RowsAsync(
				f, $"SELECT document->>'text' FROM socalytics.agent_catalog_artifacts WHERE kind = 'prompt-template' AND artifact_id = '{agent}-prompt'", ct);
			prompt.Single()[0].ShouldBe(FirstDraftPrompt(agent), agent);

			var tools = await RowsAsync(
				f, $"SELECT t.value FROM socalytics.agent_catalog_artifacts a, jsonb_array_elements_text(a.document->'tools') t(value) WHERE a.kind = 'tool-policy' AND a.artifact_id = '{agent}-tools'", ct);
			tools.Select(t => t[0]!).Order(StringComparer.Ordinal).ShouldBe(AgentCatalogIds.PermittedToolsOf(agent)!.Order(StringComparer.Ordinal), agent);
		}

		(await CountAsync(f, "agent_catalog_artifacts", ct)).ShouldBe(7);
		(await f.ScalarAsync(
			"SELECT count(*) FROM socalytics.agent_catalog_artifacts WHERE kind IN ('dimension-set', 'philosophy-profile', 'modifier-policy') OR artifact_id LIKE 'coach-%'",
			ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_versions WHERE agent_group = 'coach' OR agent_id LIKE 'coach-%'", ct)).ShouldBe(0);
	}

	[Fact]
	public async Task ASeededCatalogIsNotSeededAgain()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct);
		var artifacts = await CountAsync(f, "agent_catalog_artifacts", ct);
		var versions = await CountAsync(f, "agent_versions", ct);
		var audit = await CountAsync(f, "security_audit_event", ct);

		await RunSeederAsync(f, "Test", ct);

		(await CountAsync(f, "agent_catalog_artifacts", ct)).ShouldBe(artifacts);
		(await CountAsync(f, "agent_versions", ct)).ShouldBe(versions);
		(await CountAsync(f, "security_audit_event", ct)).ShouldBe(audit);
	}

	[Fact]
	public async Task ANonEmptyCatalogIsNotSeeded()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, SeedOff);
		(await CountAsync(f, "agent_catalog_artifacts", ct)).ShouldBe(0);
		using var registered = await new AgentCatalogApi(f.Admin).RegisterArtifactAsync(CatalogFixtures.SpecialistTacticsArtifacts().Prompt, ct);
		registered.StatusCode.ShouldBe(HttpStatusCode.Created, await registered.Content.ReadAsStringAsync(ct));

		await RunSeederAsync(f, "Test", ct);

		(await CountAsync(f, "agent_catalog_artifacts", ct)).ShouldBe(1);
		(await CountAsync(f, "agent_versions", ct)).ShouldBe(0);
		(await f.ScalarAsync("SELECT count(*) FROM socalytics.agent_enablements WHERE agent_version IS NOT NULL", ct)).ShouldBe(0);
	}

	[Theory]
	[InlineData("Production")]
	[InlineData("Staging")]
	public async Task TheSeedDoesNotRunInAnotherEnvironment(string environment)
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(postgres, ct, SeedOff);

		await RunSeederAsync(f, environment, ct);

		(await CountAsync(f, "agent_catalog_artifacts", ct)).ShouldBe(0);
		(await CountAsync(f, "agent_versions", ct)).ShouldBe(0);

		await RunSeederAsync(f, "Test", ct);

		(await CountAsync(f, "agent_versions", ct)).ShouldBe(2);
	}

	// Evidence: specs/20261025-003459-deterministic-agent-workload-test (comments only; agents cannot change files under specs/).
	// Baseline: build of src/platform/SocAlytics.Platform.slnx succeeded (0 warnings, 0 errors).
	// Baseline: SeedTests (--filter "FullyQualifiedName~SeedTests"): 6 passed, 0 failed, 0 skipped (total 6).
	// Reproduction: unchanged test run alone 30 times (4-core runner): 30 passed, 0 failed; no failure message observed.
	// Reproduction: unchanged test run 10 times under CPU load (4 busy-loop processes, one per core): 10 passed, 0 failed; no failure message observed.
	// Reproduction: the failure did not reproduce naturally; the causes are to be established by the deliberate widening of the suspected windows.
	// Widening: hold-publish. Change (temporary, reverted): in the named test, StartAsync configureServices replaced IMessageTransport (the internal NatsJetStreamTransport in Infrastructure/Messaging/Nats) by a wrapper whose first PublishAsync call throws MessageTransportException("transport-unavailable") and later calls delegate to the real transport; the wait limit was lowered from 120 s to 25 s so the failure is quick.
	// Widening: hold-publish result of the old test: failed after 27 s (invocation.ShouldNotBeNull(), no state in the message).
	// Widening: hold-publish rows: outbox_messages state=pending, attempt_count=1, next_attempt_at=2026-10-27 06:32:56 (observed, set from the frozen fixture clock at the failed publication plus backoff); agent_attempts: no row; agent_invocations.state=queued. The retry never became due because OutboxPublicationStore selects next_attempt_at <= @now and the fixture MutableTimeProvider (set once to UtcNow in AgentOrchestrationFixture.StartAsync) never advances; the runner never received a work message.
	// Widening: delay-topology. Change (temporary, reverted): same wrapper, but the first PublishAsync call awaits Task.Delay(4000 ms) before delegating (the stream and consumer exist only after the first publish, so the runner started 4 s without its consumer); wait limit 25 s.
	// Widening: delay-topology result of the old test: passed in 7 s (the runner's RetryDelay of 200 ms retried until the consumer existed); no non-terminal rows to read.
	// Widening: clock-back and clock-ahead. Change (temporary, reverted): in the named test right after StartAsync, f.Factory.Time.Set(DateTimeOffset.UtcNow.AddMinutes(-10)) and then AddMinutes(10), before SeedRunAsync; wait limit 25 s.
	// Widening: clock-back and clock-ahead result of the old test: both passed in 3 s (a frozen reading offset from the wall clock is harmless while no first publication fails); no non-terminal rows to read.
	// Suspects: wall-clock versus fake time = cause (hold-publish: old test failed after 27 s with the outbox row pending and attempt_count=1 because the frozen fixture clock never reaches next_attempt_at; a frozen clock merely offset from the wall clock passed, 2 of 2).
	// Suspects: start order (runner before topology or work message) = ruled out (delay-topology: old test passed, the runner's 200 ms RetryDelay retries until the consumer exists; 30 of 30 alone and 10 of 10 under load passed).
	// Suspects: polling loop = cause of the missing diagnosis, not of the failure (the wait polls to a guessed 120 s bound and fails with invocation.ShouldNotBeNull(), hiding the invocation state; hold-publish ended with the state queued unreported).
	// Suspects: shared state = ruled out (stream, consumer, and database names are unique per fixture and no run left state behind: 40 of 40 natural runs passed, and the widened runs failed or passed deterministically).
	// Suspects: covered production code = ruled out (the outbox selects next_attempt_at <= now and retries after a failed publication as documented; the defect is the test clock that never advances).
	// Cause: the first outbox publication of the invocation can fail transiently (for example a JetStream publish while the topology is provisioned); the retry is gated by next_attempt_at against the fixture MutableTimeProvider (AgentOrchestrationFixture.StartAsync, IdentityAccess/Support/MutableTimeProvider.cs), which is set once and never advances, so the work message is never published and the test waits out its bound without naming the state. Fix files: T/AgentOrchestration/Support/AgentOrchestrationFixture.cs and T/IdentityAccess/Support/MutableTimeProvider.cs (one clock that follows real time), and the wait in T/AgentOrchestration/Catalog/SeedTests.cs (poll to a terminal state, fail with state and reason). Decision 2 rows: "Frozen fixture clock" and "Wait ends on a guessed bound" apply; "Topology", "Leftover or shared state", and "Covered production code" do not.
	// Ruled out: SimulatedAgentWorkloadRunner and the covered production code (outbox, WorkerLoop, AgentAttemptMonitorWorker) are not the cause and are unchanged; the workload stays a development-only simulation.
	// Fix: MutableTimeProvider.FollowRealTime keeps the fixture clock running from its set value, and AgentOrchestrationFixture.StartAsync enables it for fixtures with a live workload (ConnectionStrings:nats), so a retry time stored from the clock falls due; the named test polls to a terminal state and fails with the observed state and failureReason, with the linked token as the only time-based safety net.
	// Verification: fixed test run alone 30 times (4-core runner): 30 passed, 0 failed; 10 times under CPU load (4 busy-loop processes): 10 passed, 0 failed (SC-001).
	// Verification: --filter "FullyQualifiedName~AgentOrchestration" (SeedTests, SimulatedAgentWorkloadTests, SimulatedAgentWorkloadBehaviorTests, every user of the changed fixture): 158 passed, 0 failed, 0 skipped (SC-002).
	// Verification: whole Integration.Tests project (MutableTimeProvider changed; classes run in parallel): 1944 passed, 1 skipped, 1 failed (FactPayloadPerformanceTests, a timing-budget test unrelated to the change: 2430 ms against a 2354 ms budget); it passed on rerun (2 of 2).
	// Verification: widenings applied again on top of the fix (temporary wrapper around IMessageTransport, reverted): hold-publish (first publication throws transport-unavailable, the old test failed after 27 s) passed in 6 s; delay-topology (first publication delayed 4000 ms, runner running before the work message and its consumer) passed in 8 s; forced redelivery (every agent work message published a second time with another message id) passed in 4 s.
	// Verification: shared state: the fixture is held with await using, so the runner and the NATS stream and consumer of a failed test are disposed; names are unique per fixture and the parallel full run showed no cross-test failure.
	// Probes: wrong advice text (expected text with " PROBE" appended, temporary, reverted): the named test failed on the advice assertion (invocation.GetProperty("advice").GetString()) in 4 s.
	// Probes: wrong pinned digest (expected digest with "PROBE" appended, temporary, reverted): the named test failed on the digest assertion (pin.GetProperty("digest").GetString()) in 4 s.
	// Probes: missing pinned artifact kind (expected kinds without "safety-policy", temporary, reverted): the named test failed on the artifact kinds assertion (pin.GetProperty("artifacts")...ShouldBe) in 4 s.
	// Probes: every probe was removed afterwards and git diff shows no probe.
	[Fact]
	public async Task TheSimulatedAgentWorkloadCompletesAnInvocationOfTheSeededVersionWithItsPinnedReferences()
	{
		var ct = TestContext.Current.CancellationToken;
		await using var f = await AgentOrchestrationFixture.StartAsync(
			postgres, ct,
			s =>
			{
				s["ConnectionStrings:nats"] = nats.ConnectionString;
				s["Messaging:Nats:ProvisionTopology"] = "true";
			});
		var runId = await f.SeedRunAsync(SeededRunState.Completed, ct: ct);

		async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string? ifMatch, CancellationToken token)
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

			using var response = await f.Admin.Client.SendAsync(request, token);
			((int)response.StatusCode).ShouldBeInRange(200, 202, await response.Content.ReadAsStringAsync(token));
			var element = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token)).RootElement.Clone();
			return method == HttpMethod.Post && response.Headers.ETag is { } etag
				? JsonDocument.Parse($"{{\"etag\":\"{etag.Tag.Replace("\"", "\\\"")}\",\"body\":{element.GetRawText()}}}").RootElement.Clone()
				: element;
		}

		var created = await SendAsync(HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		var conversationId = created.GetProperty("body").GetProperty("conversationId").GetGuid();
		await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, created.GetProperty("etag").GetString(), ct);
		var submitted = await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = "What should we change?" }, null, ct);
		var invocationId = (submitted.TryGetProperty("body", out var wrapped) ? wrapped : submitted).GetProperty("invocationId").GetGuid();

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
		await using var runner = new SimulatedAgentWorkloadRunner(options, http, f.Factory.Time);
		using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
		var limit = TimeSpan.FromSeconds(120);
		run.CancelAfter(limit + TimeSpan.FromSeconds(5));
		var worker = runner.RunAsync(run.Token);

		// The success condition is a terminal state; the linked token is only the safety net against a hung platform.
		JsonElement invocation = default;
		string? state = null;
		string? failureReason = null;
		try
		{
			while (true)
			{
				invocation = await SendAsync(HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, run.Token);
				state = invocation.GetProperty("state").GetString();
				failureReason = invocation.TryGetProperty("failureReason", out var reason) && reason.ValueKind == JsonValueKind.String ? reason.GetString() : null;
				if (state is "succeeded" or "failed" or "lifecycle-suppressed")
				{
					break;
				}

				await Task.Delay(250, run.Token);
			}
		}
		catch (OperationCanceledException) when (run.IsCancellationRequested && !ct.IsCancellationRequested)
		{
			Assert.Fail($"The invocation {invocationId} did not reach a terminal state within {limit.TotalSeconds:0} s: state '{state}', failureReason '{failureReason}'.");
		}
		finally
		{
			await run.CancelAsync();
			await worker;
		}

		state.ShouldBe("succeeded", $"failureReason: '{failureReason}'");
		invocation.GetProperty("advice").GetString().ShouldBe($"Simulated advice from {Tactics}");
		var pin = invocation.GetProperty("agent");
		pin.GetProperty("version").GetString().ShouldBe(SeedVersion);
		var digest = await RowsAsync(f, $"SELECT digest FROM socalytics.agent_versions WHERE agent_id = '{Tactics}' AND agent_version = '{SeedVersion}'", ct);
		pin.GetProperty("digest").GetString().ShouldBe(digest.Single()[0]);
		pin.GetProperty("artifacts").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).Order()
			.ShouldBe(["model-selection", "prompt-template", "safety-policy", "tool-policy"]);
	}
}

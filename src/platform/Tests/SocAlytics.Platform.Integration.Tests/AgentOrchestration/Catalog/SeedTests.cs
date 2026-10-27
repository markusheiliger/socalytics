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

		async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, string? ifMatch)
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

			using var response = await f.Admin.Client.SendAsync(request, ct);
			((int)response.StatusCode).ShouldBeInRange(200, 202, await response.Content.ReadAsStringAsync(ct));
			var element = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
			return method == HttpMethod.Post && response.Headers.ETag is { } etag
				? JsonDocument.Parse($"{{\"etag\":\"{etag.Tag.Replace("\"", "\\\"")}\",\"body\":{element.GetRawText()}}}").RootElement.Clone()
				: element;
		}

		var created = await SendAsync(HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null);
		var conversationId = created.GetProperty("body").GetProperty("conversationId").GetGuid();
		await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, created.GetProperty("etag").GetString());
		var submitted = await SendAsync(
			HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations", new { agentId = Tactics, question = "What should we change?" }, null);
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

		JsonElement? invocation = null;
		var deadline = DateTime.UtcNow.Add(limit);
		while (invocation is null && DateTime.UtcNow < deadline)
		{
			var read = await SendAsync(HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null);
			if (read.GetProperty("state").GetString() == "succeeded")
			{
				invocation = read;
			}
			else
			{
				await Task.Delay(250, ct);
			}
		}

		await run.CancelAsync();
		await worker;

		invocation.ShouldNotBeNull();
		invocation.Value.GetProperty("advice").GetString().ShouldBe($"Simulated advice from {Tactics}");
		var pin = invocation.Value.GetProperty("agent");
		pin.GetProperty("version").GetString().ShouldBe(SeedVersion);
		var digest = await RowsAsync(f, $"SELECT digest FROM socalytics.agent_versions WHERE agent_id = '{Tactics}' AND agent_version = '{SeedVersion}'", ct);
		pin.GetProperty("digest").GetString().ShouldBe(digest.Single()[0]);
		pin.GetProperty("artifacts").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).Order()
			.ShouldBe(["model-selection", "prompt-template", "safety-policy", "tool-policy"]);
	}
}

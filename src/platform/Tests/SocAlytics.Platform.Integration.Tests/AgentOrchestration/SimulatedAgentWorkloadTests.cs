using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.AgentOrchestration.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.SimulatedAgentWorkload;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.AgentOrchestration;

public sealed class SimulatedAgentWorkloadTests(PostgresContainerFixture postgres, NatsContainerFixture nats)
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

	[Fact]
	public async Task RunnerClaimsAndCompletesASubmittedInvocationSoTheOwnerReadsSimulatedAdvice()
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

		using var created = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/matches/{f.Hierarchy.MatchId}/agent-conversations", new { analysisRunId = runId }, null, ct);
		created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(ct));
		var etag = created.Headers.ETag!.Tag;
		var conversationId = (await ReadAsync(created, ct)).GetProperty("conversationId").GetGuid();
		using var added = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/participants", new { agentId = Tactics }, etag, ct);
		added.StatusCode.ShouldBe(HttpStatusCode.OK, await added.Content.ReadAsStringAsync(ct));

		using var submitted = await MemberAsync(
			f, HttpMethod.Post, $"/api/v1/agent-conversations/{conversationId}/invocations",
			new { agentId = Tactics, question = "What should we change?" }, null, ct);
		submitted.StatusCode.ShouldBe(HttpStatusCode.Accepted, await submitted.Content.ReadAsStringAsync(ct));
		var invocationId = (await ReadAsync(submitted, ct)).GetProperty("invocationId").GetGuid();

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

		string? advice = null;
		var deadline = DateTime.UtcNow.Add(limit);
		while (advice is null && DateTime.UtcNow < deadline)
		{
			using var read = await MemberAsync(f, HttpMethod.Get, $"/api/v1/agent-invocations/{invocationId}", null, null, ct);
			var body = await ReadAsync(read, ct);
			if (body.GetProperty("state").GetString() == "succeeded")
			{
				advice = body.GetProperty("advice").GetString();
			}
			else
			{
				await Task.Delay(250, ct);
			}
		}

		await run.CancelAsync();
		await worker;

		advice.ShouldNotBeNull();
		advice.ShouldStartWith($"Simulated advice from {Tactics}.");
		advice.ShouldContain("Simulated tool outcomes:");
	}

	[Fact]
	public async Task ExecutableRefusesToStartInProduction()
	{
		var ct = TestContext.Current.CancellationToken;
		// The executable runs from its own build output, which carries its own dependency manifest.
		var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
		var framework = output.Name;
		var configuration = output.Parent!.Name;
		var assembly = Path.GetFullPath(Path.Combine(
			output.Parent.Parent!.Parent!.Parent!.FullName, "SocAlytics.Platform.SimulatedAgentWorkload", "bin", configuration, framework,
			"SocAlytics.Platform.SimulatedAgentWorkload.dll"));
		File.Exists(assembly).ShouldBeTrue();
		var start = new ProcessStartInfo("dotnet", $"\"{assembly}\"")
		{
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		start.Environment["DOTNET_ENVIRONMENT"] = "Production";
		using var process = Process.Start(start)!;
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeout.CancelAfter(TimeSpan.FromSeconds(60));
		var error = await process.StandardError.ReadToEndAsync(timeout.Token);
		await process.WaitForExitAsync(timeout.Token);

		process.ExitCode.ShouldBe(1);
		error.ShouldContain("Development or Test");
	}
}

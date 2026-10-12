using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.Analysis.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis;

[Collection("NatsFaultInjection")]
public sealed class PayloadConformanceTests(PostgresContainerFixture postgres, RustFsContainerFixture rustFs, NatsContainerFixture nats)
{
    private const string Digest = "sha-256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static DirectoryInfo RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json")))
        {
            dir = dir.Parent;
        }

        return dir;
    }

    private Dictionary<string, string?> WorkerSettings()
    {
        var path = Path.Combine(RepositoryRoot().FullName, "src", "platform", "SocAlytics.Platform.Api", "appsettings.Development.json");
        var settings = new ConfigurationBuilder().AddJsonFile(path).Build().GetSection("Analysis")
            .AsEnumerable(true).ToDictionary(p => "Analysis:" + p.Key, p => p.Value);
        settings["ConnectionStrings:nats"] = nats.ConnectionString;
        settings["Messaging:Nats:ProvisionTopology"] = "true";
        settings["Analysis:Workers:Enabled"] = "true";
        return settings;
    }

    private async Task<FinalizationSetup> StartAsync(CancellationToken ct)
    {
        var setup = await FinalizationSetup.StartAsync(postgres, rustFs, ct, sets: null, configureServices: services =>
            services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(o =>
                o.Schemes.Single(x => x.Name == SocAlytics.Platform.Api.Registry.AnalystManagerDPoPDefaults.AuthenticationScheme).HandlerType =
                    typeof(TestManagerAuthenticationHandler)), extraSettings: WorkerSettings());
        await RecordingFinalizationHelper.FinalizeAsync(setup.Coach, setup.Hierarchy.MatchId, 1, ct);
        return setup;
    }

    private async Task<List<(string Subject, JsonElement Payload)>> ReadMessagesAsync(CancellationToken ct)
    {
        var all = new List<(string, JsonElement)>();
        foreach (var name in new[] { "SOCALYTICS_ANALYSIS_JOBS", "SOCALYTICS_EVENTS" })
        {
            all.AddRange(await ReadStreamAsync(name, ct));
        }

        return all;
    }

    private async Task<List<(string Subject, JsonElement Payload)>> ReadStreamAsync(string name, CancellationToken ct)
    {
        await using var connection = new NatsConnection(NatsOpts.Default with { Url = nats.ConnectionString });
        var stream = await new NatsJSContext(connection).GetStreamAsync(name, cancellationToken: ct);
        var result = new List<(string, JsonElement)>();
        var info = stream.Info.State;
        for (var seq = info.FirstSeq; seq <= info.LastSeq && info.Messages > 0; seq++)
        {
            try
            {
                var raw = await stream.GetAsync(new StreamMsgGetRequest { Seq = seq }, ct);
                result.Add((raw.Message.Subject, JsonDocument.Parse(raw.Message.Data.ToArray()).RootElement.Clone()));
            }
            catch (NatsJSApiException)
            {
            }
        }

        return result;
    }

    private async Task<List<(string Subject, JsonElement Payload)>> WaitForReadyWorkAsync(FinalizationSetup s, CancellationToken ct)
    {
        for (var i = 0; i < 120; i++)
        {
            var messages = await ReadMessagesAsync(ct);
            if (messages.Any(m => m.Subject.StartsWith("analysis.jobs.ready.")))
            {
                return messages;
            }

            s.Factory.Time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(500, ct);
        }

        throw new TimeoutException("No ready-work message was published.");
    }

    private static string SchemaFor(string subject) => subject switch
    {
        "analysis.events.run-state-changed" => ContractSchemaIds.RunStateChangedV1,
        _ when subject.StartsWith("analysis.jobs.ready.") => ContractSchemaIds.AnalystJobV1,
        _ => throw new InvalidOperationException(subject),
    };

    private static JsonObject ValidBody(JsonElement job, Guid attempt, long token, int number, string key) => new()
    {
        ["contractVersion"] = "1.0.0",
        ["idempotencyKey"] = key,
        ["teamId"] = job.GetProperty("teamId").GetGuid(),
        ["matchId"] = job.GetProperty("matchId").GetGuid(),
        ["runId"] = job.GetProperty("runId").GetGuid(),
        ["nodeId"] = job.GetProperty("nodeId").GetGuid(),
        ["logicalJobId"] = job.GetProperty("logicalJobId").GetGuid(),
        ["attemptId"] = attempt,
        ["attemptNumber"] = number,
        ["fencingToken"] = token,
        ["outcome"] = "succeeded",
        ["result"] = new JsonObject { ["manifestReference"] = "results/m1.json", ["manifestChecksum"] = Digest },
        ["capability"] = JsonNode.Parse(job.GetProperty("capability").GetRawText()),
        ["schemaVersion"] = JsonNode.Parse(job.GetProperty("schemaVersion").GetRawText()),
        ["executedRuntime"] = new JsonObject
        {
            ["runtimeProfile"] = "cpu", ["runtimeVersion"] = "1.0", ["operatingSystem"] = "linux",
            ["architecture"] = "x64", ["accelerators"] = new JsonArray(),
        },
        ["executedImageDigest"] = JsonNode.Parse(job.GetProperty("pinned").GetProperty("imageDigest").GetRawText()),
        ["executedModelDigest"] = JsonNode.Parse(job.GetProperty("pinned").GetProperty("modelDigest").GetRawText()),
        ["acceptedUpstreamResultIds"] = new JsonArray(),
        ["completedAt"] = "2026-01-01T00:00:00Z",
        ["correlation"] = new JsonObject { ["correlationId"] = Guid.NewGuid() },
    };

    private static async Task<HttpResponseMessage> CompleteAsync(FinalizationSetup s, TestManagerClient m, Guid attempt, JsonNode body, CancellationToken ct)
    {
        using var client = s.Factory.CreateApiClient();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/analysis/attempts/{attempt}/completion")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(TestManagerAuthenticationHandler.ManagerHeader, m.ManagerId.ToString());
        return await client.SendAsync(request, ct);
    }

    [Fact]
    public async Task PublishedMessagesConformToTheirSchemasAndCarryScopeIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var messages = await WaitForReadyWorkAsync(s, ct);
        var validator = s.Factory.Services.GetRequiredService<IContractValidator>();

        foreach (var (subject, payload) in messages.Where(m => m.Subject.StartsWith("analysis.jobs.ready.") || m.Subject == "analysis.events.run-state-changed"))
        {
            var result = validator.Validate(SchemaFor(subject), payload);
            result.IsValid.ShouldBeTrue($"{subject}: {string.Join(", ", result.Errors.Select(e => e.Field + ":" + e.Keyword))}");
        }

        var jobs = messages.Where(m => m.Subject.StartsWith("analysis.jobs.ready.")).Select(m => m.Payload).ToList();
        jobs.ShouldContain(j => j.GetProperty("executionScope").GetString() == "segment");
        foreach (var job in jobs)
        {
            if (job.GetProperty("executionScope").GetString() == "segment")
            {
                var segment = job.GetProperty("segment");
                segment.GetProperty("recordingSetVersionId").GetGuid().ShouldBe(job.GetProperty("recordingSetVersionId").GetGuid());
                foreach (var name in new[] { "recordingVersionId", "timelineMappingDigest", "segmentationPolicyDigest", "segmentNumber" })
                {
                    segment.TryGetProperty(name, out _).ShouldBeTrue(name);
                }
            }
            else
            {
                job.TryGetProperty("segment", out _).ShouldBeFalse();
            }
        }
    }

    [Fact]
    public async Task CompletionBodiesAreValidatedAndStaleOnesAreRejectedByAcceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        var job = (await WaitForReadyWorkAsync(s, ct)).First(m => m.Subject.StartsWith("analysis.jobs.ready.")).Payload;
        var validator = s.Factory.Services.GetRequiredService<IContractValidator>();
        using var manager = new TestManagerClient(s.Factory);

        using var claim = await manager.ClaimAsync(
            job.GetProperty("logicalJobId").GetGuid(), job.GetProperty("readinessOccurrenceId").GetGuid(), "claim-1", ct: ct);
        claim.StatusCode.ShouldBe(HttpStatusCode.Created, await claim.Content.ReadAsStringAsync(ct));
        var claimed = JsonDocument.Parse(await claim.Content.ReadAsStringAsync(ct)).RootElement;
        var attempt = claimed.GetProperty("attemptId").GetGuid();
        var token = claimed.GetProperty("fencingToken").GetInt64();
        var number = claimed.GetProperty("attemptNumber").GetInt32();

        var examples = Path.Combine(RepositoryRoot().FullName, "contracts", "analysis", "attempt-completion", "v1", "examples", "invalid");
        var files = Directory.GetFiles(examples, "*.json").Where(f => Path.GetFileName(f) != "expectations.json").ToList();
        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var body = JsonNode.Parse(await File.ReadAllTextAsync(file, ct))!.AsObject();
            foreach (var (name, value) in new (string, JsonNode)[]
            {
                ("teamId", job.GetProperty("teamId").GetGuid()), ("matchId", job.GetProperty("matchId").GetGuid()),
                ("runId", job.GetProperty("runId").GetGuid()), ("nodeId", job.GetProperty("nodeId").GetGuid()),
                ("logicalJobId", job.GetProperty("logicalJobId").GetGuid()), ("attemptId", attempt),
                ("attemptNumber", number), ("fencingToken", token),
            })
            {
                if (body.ContainsKey(name))
                {
                    body[name] = value;
                }
            }

            using var response = await CompleteAsync(s, manager, attempt, body, ct);
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, Path.GetFileName(file));
        }

        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_completion_outcomes", ct)).ShouldBe(0);
        (await s.ScalarAsync("SELECT count(*) FROM socalytics.analysis_execution_attempts WHERE state = 'Active'", ct)).ShouldBe(1);

        var stale = ValidBody(job, attempt, token + 100, number, "stale-1");
        var staleResult = validator.Validate(ContractSchemaIds.AttemptCompletionV1, JsonSerializer.SerializeToElement(stale));
        staleResult.IsValid.ShouldBeTrue(string.Join(", ", staleResult.Errors.Select(e => e.Field + ":" + e.Keyword)));
        using var rejected = await CompleteAsync(s, manager, attempt, stale, ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await rejected.Content.ReadAsStringAsync(ct)).ShouldContain("attempt-obsolete");

        var good = ValidBody(job, attempt, token, number, "good-1");
        validator.Validate(ContractSchemaIds.AttemptCompletionV1, JsonSerializer.SerializeToElement(good)).IsValid.ShouldBeTrue();
        using var accepted = await CompleteAsync(s, manager, attempt, good, ct);
        accepted.StatusCode.ShouldBe(HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(ct));
    }
}

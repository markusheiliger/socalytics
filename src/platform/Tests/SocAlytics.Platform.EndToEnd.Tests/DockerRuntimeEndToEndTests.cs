using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Analysts.Manager.Work;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.EndToEnd.Tests.Support;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.EndToEnd.Tests;

[Collection("EndToEnd")]
public sealed class DockerRuntimeEndToEndTests
{
    private const string Prefix = "analysts/";
    private const string WorkflowId = "e2e-docker-workflow";

    [Fact]
    public async Task A_ready_job_runs_in_a_restricted_container_and_is_accepted_once()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;

        var registry = new OciRegistryContainerFixture();
        await registry.InitializeAsync();
        PushedAnalystImage? image = null;
        string? compatibilityPath = null;
        try
        {
            var started = await EndToEndHarness.StartAsync(ct, new Dictionary<string, string?>
            {
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            });
            if (started is null)
            {
                Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
                return;
            }

            await using var harness = started;
            image = await TestAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct);
            var imageDigest = "sha-256:" + image.Digest["sha256:".Length..];
            await DefineDefaultWorkflowAsync(harness, image.Reference(registry.RegistryHost), ct);

            using var docker = new DockerEngine();
            compatibilityPath = await WriteCompatibilityListAsync(docker, ct);
            await harness.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);
            var managerId = harness.Control.GetStatus().ManagerId!;
            var managerLabel = $"io.socalytics.manager={managerId}";

            var runId = await SeedReadyJobAsync(harness, ct);

            // The container is held a few seconds so that it can be inspected while it runs.
            string? containerId = null;
            await WaitOrDiagnoseAsync(harness, runId, async () =>
            {
                foreach (var id in await docker.ListContainersAsync(managerLabel, ct))
                {
                    var inspected = await docker.GetJsonAsync($"/containers/{id}/json", ct);
                    if (inspected.GetProperty("State").GetProperty("Running").GetBoolean())
                    {
                        containerId = id;
                        return true;
                    }
                }

                return await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) > 0;
            }, "No Analyst container was started.", ct);
            containerId.ShouldNotBeNull("The container finished before it could be inspected.");

            var container = await docker.GetJsonAsync($"/containers/{containerId}/json", ct);
            var imageConfig = await docker.GetJsonAsync($"/images/{container.GetProperty("Image").GetString()}/json", ct);
            AssertRestrictions(container, imageConfig, managerId, imageDigest);
            var attemptId = Guid.Parse(container.GetProperty("Config").GetProperty("Labels").GetProperty("io.socalytics.attempt").GetString()!);

            await WaitOrDiagnoseAsync(
                harness, runId, async () => await WorkExecutionEndToEndTests.CountAcceptedAsync(harness, runId, ct) > 0,
                "No accepted result arrived in time.", ct);

            await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
            await sql.OpenAsync(ct);
            await using var command = new NpgsqlCommand(
                "SELECT image_digest, manager_id::text FROM socalytics.analysis_accepted_results r JOIN socalytics.analysis_execution_attempts a ON a.id = r.attempt_id WHERE r.run_id = @runId", sql);
            command.Parameters.AddWithValue("runId", runId);
            await using var reader = await command.ExecuteReaderAsync(ct);
            (await reader.ReadAsync(ct)).ShouldBeTrue();
            reader.GetString(0).ShouldBe(imageDigest);
            reader.GetString(1).ShouldBe(managerId);
            (await reader.ReadAsync(ct)).ShouldBeFalse("Exactly one accepted result is expected.");

            await EndToEndHarness.WaitAsync(
                async () => (await docker.ListContainersAsync(managerLabel, ct)).Count == 0 && !Directory.Exists(UpstreamStager.DirectoryFor(attemptId)),
                TimeSpan.FromSeconds(30), "A container or attempt directory of the attempt remains.", ct);
        }
        finally
        {
            if (image is not null)
            {
                await TestAnalystImage.RemoveAsync(image);
            }

            if (compatibilityPath is not null)
            {
                File.Delete(compatibilityPath);
            }

            await registry.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("fail", "analyst-error")]
    [InlineData("no-manifest", "analyst-error")]
    [InlineData("oom", "resource-exhausted")]
    public async Task A_failing_Analyst_ends_with_one_failed_completion_of_the_expected_category_and_leaves_no_container(string mode, string category)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        await using var scenario = await Scenario.StartAsync(mode, ct);
        if (scenario is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        var runId = await SeedReadyJobAsync(scenario.Harness, ct);
        await WaitOrDiagnoseAsync(
            scenario.Harness, runId, async () => (await AttemptsAsync(scenario.Harness, runId, ct)).Any(a => a.State == "Failed"),
            "The attempt did not fail.", ct);

        var attempts = await AttemptsAsync(scenario.Harness, runId, ct);
        attempts.Count.ShouldBe(1);
        attempts[0].State.ShouldBe("Failed");
        attempts[0].CloseReason.ShouldBe(category);
        (await WorkExecutionEndToEndTests.CountAcceptedAsync(scenario.Harness, runId, ct)).ShouldBe(0);
        await EndToEndHarness.WaitAsync(
            async () => (await scenario.Docker.ListContainersAsync(scenario.ManagerLabel, ct)).Count == 0
                && !Directory.Exists(UpstreamStager.DirectoryFor(attempts[0].Id)),
            TimeSpan.FromSeconds(30), "A container or attempt directory of the attempt remains.", ct);
    }

    [Fact]
    public async Task Revoking_a_Manager_during_a_hanging_Analyst_stops_and_removes_the_container()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        await using var scenario = await Scenario.StartAsync("hang", ct);
        if (scenario is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        var runId = await SeedReadyJobAsync(scenario.Harness, ct);
        var (containerId, attemptId) = await WaitForRunningContainerAsync(scenario, runId, ct);

        await scenario.Harness.RevokeManagerAsync(scenario.Manager, ct);

        await EndToEndHarness.WaitAsync(
            async () => !await scenario.Docker.ExistsAsync(containerId, ct) && (await scenario.Docker.ListContainersAsync(scenario.ManagerLabel, ct)).Count == 0
                && !Directory.Exists(UpstreamStager.DirectoryFor(attemptId)),
            TimeSpan.FromSeconds(60), "The container or attempt directory of the revoked Manager remains.", ct);
    }

    [Fact]
    public async Task A_restarted_Manager_removes_its_leftover_container_and_directory_but_not_an_unlabelled_container()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = cts.Token;
        await using var scenario = await Scenario.StartAsync("hang", ct, leaseSeconds: 3, staleTimeoutSeconds: 6);
        if (scenario is null)
        {
            Assert.Skip("SoftHSM2 (libsofthsm2 and softhsm2-util) is required on Linux for the Manager's device key.");
            return;
        }

        string? unlabelled = null;
        string? leftover = null;
        try
        {
            var runId = await SeedReadyJobAsync(scenario.Harness, ct);
            var (containerId, attemptId) = await WaitForRunningContainerAsync(scenario, runId, ct);
            var attemptDirectory = UpstreamStager.DirectoryFor(attemptId);

            await scenario.Harness.KillManagerAsync(scenario.Manager);

            // Stopping the host cleans up gracefully; put back what a crash would have left behind.
            if (!await scenario.Docker.ExistsAsync(containerId, ct))
            {
                leftover = await scenario.Docker.StartSleepingContainerAsync(
                    scenario.Image.LocalName,
                    new Dictionary<string, string>
                    {
                        ["io.socalytics.manager"] = scenario.ManagerId,
                        ["io.socalytics.attempt"] = attemptId.ToString(),
                    }, ct);
            }

            Directory.CreateDirectory(Path.Combine(attemptDirectory, "result"));
            unlabelled = await scenario.Docker.StartSleepingContainerAsync(scenario.Image.LocalName, new Dictionary<string, string>(), ct);

            await EndToEndHarness.WaitAsync(
                async () => (await AttemptsAsync(scenario.Harness, runId, ct)).Any(a => a.State == "Stale"),
                TimeSpan.FromSeconds(60), "The abandoned attempt was not expired by the lease.", ct);

            await scenario.Harness.StartManagerAsync(ct, dockerCompatibilityListPath: scenario.CompatibilityPath, restartOf: scenario.Manager);

            await EndToEndHarness.WaitAsync(
                async () => (await scenario.Docker.ListContainersAsync(scenario.ManagerLabel, ct)).Count == 0 && !Directory.Exists(attemptDirectory),
                TimeSpan.FromSeconds(60), "The leftover container or attempt directory was not removed.", ct);
            (await scenario.Docker.IsRunningAsync(unlabelled, ct)).ShouldBeTrue("The unlabelled container must stay untouched.");
            (await WorkExecutionEndToEndTests.CountAcceptedAsync(scenario.Harness, runId, ct)).ShouldBe(0);
            (await AttemptsAsync(scenario.Harness, runId, ct)).Single().State.ShouldBe("Stale");
        }
        finally
        {
            foreach (var id in new[] { unlabelled, leftover }.OfType<string>())
            {
                await scenario.Docker.RemoveContainerAsync(id);
            }
        }
    }

    private sealed record AttemptRow(Guid Id, string State, string? CloseReason);

    private static async Task<List<AttemptRow>> AttemptsAsync(EndToEndHarness harness, Guid runId, CancellationToken ct)
    {
        await using var sql = new NpgsqlConnection(harness.Database.MigratorConnectionString);
        await sql.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT a.id, a.state, a.close_reason
            FROM socalytics.analysis_execution_attempts a
            JOIN socalytics.analysis_logical_jobs j ON j.id = a.logical_job_id
            WHERE j.run_id = @runId
            ORDER BY a.attempt_number
            """, sql);
        command.Parameters.AddWithValue("runId", runId);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<AttemptRow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new AttemptRow(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<(string ContainerId, Guid AttemptId)> WaitForRunningContainerAsync(Scenario scenario, Guid runId, CancellationToken ct)
    {
        string? containerId = null;
        Guid attemptId = default;
        await WaitOrDiagnoseAsync(scenario.Harness, runId, async () =>
        {
            foreach (var id in await scenario.Docker.ListContainersAsync(scenario.ManagerLabel, ct))
            {
                var inspected = await scenario.Docker.GetJsonAsync($"/containers/{id}/json", ct);
                if (inspected.GetProperty("State").GetProperty("Running").GetBoolean())
                {
                    containerId = id;
                    attemptId = Guid.Parse(inspected.GetProperty("Config").GetProperty("Labels").GetProperty("io.socalytics.attempt").GetString()!);
                    return true;
                }
            }

            return false;
        }, "No Analyst container was started.", ct);
        return (containerId!, attemptId);
    }

    /// <summary>The platform, a registry with one failure-mode Test Analyst, and a Docker-backed Manager that offers it.</summary>
    private sealed class Scenario : IAsyncDisposable
    {
        private readonly OciRegistryContainerFixture _registry;

        private Scenario(
            OciRegistryContainerFixture registry, EndToEndHarness harness, PushedAnalystImage image, DockerEngine docker, string compatibilityPath,
            Microsoft.Extensions.Hosting.IHost manager)
        {
            _registry = registry;
            Harness = harness;
            Image = image;
            Docker = docker;
            CompatibilityPath = compatibilityPath;
            Manager = manager;
            ManagerId = harness.Control.GetStatus().ManagerId!;
            ManagerLabel = $"io.socalytics.manager={ManagerId}";
        }

        public EndToEndHarness Harness { get; }

        public PushedAnalystImage Image { get; }

        public DockerEngine Docker { get; }

        public string CompatibilityPath { get; }

        public Microsoft.Extensions.Hosting.IHost Manager { get; }

        public string ManagerId { get; }

        public string ManagerLabel { get; }

        /// <summary>Returns null when SoftHSM2 is unavailable.</summary>
        public static async Task<Scenario?> StartAsync(string mode, CancellationToken ct, int leaseSeconds = 60, int staleTimeoutSeconds = 120)
        {
            var registry = new OciRegistryContainerFixture();
            await registry.InitializeAsync();
            var started = await EndToEndHarness.StartAsync(ct, new Dictionary<string, string?>
            {
                ["Registry:Images:RegistryHost"] = registry.RegistryHost,
                ["Registry:Images:RepositoryPrefix"] = Prefix,
                ["Registry:Images:UseHttp"] = "true",
            });
            if (started is null)
            {
                await registry.DisposeAsync();
                return null;
            }

            var image = await TestAnalystImage.BuildAndPushAsync(registry.RegistryHost, Prefix, ct, mode);
            var docker = new DockerEngine();
            var compatibilityPath = await WriteCompatibilityListAsync(docker, ct);
            await DefineDefaultWorkflowAsync(
                started, image.Reference(registry.RegistryHost), ct, mode, maxAttempts: 1, leaseSeconds: leaseSeconds, staleTimeoutSeconds: staleTimeoutSeconds);
            var manager = await started.StartManagerAsync(ct, dockerCompatibilityListPath: compatibilityPath);
            return new Scenario(registry, started, image, docker, compatibilityPath, manager);
        }

        public async ValueTask DisposeAsync()
        {
            await Harness.DisposeAsync();
            await TestAnalystImage.RemoveAsync(Image);
            Docker.Dispose();
            File.Delete(CompatibilityPath);
            await _registry.DisposeAsync();
        }
    }

    private static void AssertRestrictions(JsonElement container, JsonElement imageConfig, string managerId, string imageDigest)
    {
        var config = container.GetProperty("Config");
        var host = container.GetProperty("HostConfig");

        var labels = config.GetProperty("Labels");
        labels.GetProperty("io.socalytics.manager").GetString().ShouldBe(managerId);
        labels.TryGetProperty("io.socalytics.logical-job", out _).ShouldBeTrue();
        labels.TryGetProperty("io.socalytics.attempt", out _).ShouldBeTrue();

        var user = config.GetProperty("User").GetString();
        user.ShouldNotBeNullOrEmpty();
        user.Split(':')[0].ShouldNotBe("0");
        user.ShouldNotBe("root");

        // The Manager sets no variables; the only ones present come from the image itself.
        var imageEnv = imageConfig.GetProperty("Config").GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        config.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).Where(e => !imageEnv.Contains(e)).ShouldBeEmpty();

        host.GetProperty("NetworkMode").GetString().ShouldBe("none");
        host.GetProperty("ReadonlyRootfs").GetBoolean().ShouldBeTrue();
        host.GetProperty("Privileged").GetBoolean().ShouldBeFalse();
        host.GetProperty("CapDrop").EnumerateArray().Select(c => c.GetString()).ShouldBe(["ALL"]);
        host.GetProperty("CapAdd").ValueKind.ShouldBeOneOf(JsonValueKind.Null, JsonValueKind.Array);
        if (host.GetProperty("CapAdd").ValueKind == JsonValueKind.Array)
        {
            host.GetProperty("CapAdd").GetArrayLength().ShouldBe(0);
        }

        host.GetProperty("SecurityOpt").EnumerateArray().Select(o => o.GetString()).ShouldContain(o => o!.StartsWith("no-new-privileges"));
        (host.TryGetProperty("Devices", out var devices) && devices.ValueKind == JsonValueKind.Array ? devices.GetArrayLength() : 0).ShouldBe(0);
        host.GetProperty("PidMode").GetString().ShouldBeNullOrEmpty();
        host.GetProperty("IpcMode").GetString().ShouldNotBe("host");

        const long memory = 256L * 1024 * 1024;
        host.GetProperty("Memory").GetInt64().ShouldBe(memory);
        host.GetProperty("MemorySwap").GetInt64().ShouldBe(memory);
        host.GetProperty("NanoCpus").GetInt64().ShouldBe(1_000_000_000L);
        host.GetProperty("PidsLimit").GetInt64().ShouldBe(512);
        host.GetProperty("Tmpfs").GetProperty("/tmp").GetString()!.ShouldContain($"size={64 * 1024 * 1024}");
        var log = host.GetProperty("LogConfig");
        log.GetProperty("Type").GetString().ShouldBe("json-file");
        log.GetProperty("Config").GetProperty("max-size").GetString().ShouldBe("10m");

        var binds = host.GetProperty("Binds").EnumerateArray().Select(b => b.GetString()!).ToList();
        binds.ShouldContain(b => b.Contains("job.json:/socalytics/job.json:ro"));
        binds.ShouldContain(b => b.Contains(":/socalytics/result:rw"));
        binds.ShouldContain(b => b.Contains(":/socalytics/scratch:rw"));
        binds.ShouldAllBe(b => !b.Contains("docker.sock"));

        // The container was created from the pinned digest, not a tag.
        imageConfig.GetProperty("RepoDigests").EnumerateArray().Select(d => d.GetString()).ShouldContain(d => d!.EndsWith("@sha256:" + imageDigest.Substring(8)));
    }

    private static async Task DefineDefaultWorkflowAsync(
        EndToEndHarness harness, string reference, CancellationToken ct, string mode = "succeed", int maxAttempts = 3, int leaseSeconds = 60,
        int staleTimeoutSeconds = 120)
    {
        var capabilityId = mode == "succeed" ? TestAnalystImage.CapabilityId : TestAnalystImage.CapabilityFor(mode);
        var profileId = mode == "succeed" ? TestAnalystImage.ProfileId : TestAnalystImage.ProfileFor(mode);
        var admin = harness.Admin;
        using var registered = await admin.SendAsync(HttpMethod.Post, "/api/v1/registry/analyst-images", new { reference }, ct);
        registered.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        var imageDigest = (await Read(registered, ct)).GetProperty("imageDigest").GetString();

        var content = JsonNode.Parse($$"""
            {
              "capabilityId": "{{capabilityId}}",
              "capabilityVersion": "1.0.0",
              "modelDigest": "{{TestAnalystImage.ModelDigest}}",
              "runtimeConstraints": ["linux-amd64"],
              "accelerators": [],
              "resources": { "memory": "256Mi", "cpu": "1" },
              "maxAttempts": {{maxAttempts}},
              "leaseSeconds": {{leaseSeconds}},
              "staleTimeoutSeconds": {{staleTimeoutSeconds}},
              "executionTimeoutSeconds": 900,
              "videoPolicy": "forbidden",
              "inputSchemaVersions": {},
              "outputSchemaVersion": "1.0.0"
            }
            """)!;
        using var created = await admin.SendAsync(
            HttpMethod.Post, "/api/v1/registry/analyst-profiles", new { profileId, imageDigest, content }, ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var version = (await Read(created, ct)).GetProperty("version").GetInt32();

        var approval = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/registry/analyst-profiles/{profileId}/versions/{version}/approval");
        approval.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
        approval.Headers.TryAddWithoutValidation("If-Match", created.Headers.ETag!.Tag);
        using var approved = await admin.Client.SendAsync(approval, ct);
        approved.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var workflow = await admin.SendAsync(HttpMethod.Post, "/api/v1/registry/workflows", new
        {
            workflowId = WorkflowId,
            workflowVersion = "1",
            requestedCapabilities = new[] { capabilityId },
            completionPolicy = "all-requested-capabilities",
            segmentationPolicy = new { version = 1, durationSeconds = 60, digest = "sha-256:" + new string('1', 64) },
            nodes = new[] { new { nodeKey = "analyst", profileId, profileVersion = version } },
            edges = Array.Empty<object>(),
        }, ct);
        workflow.StatusCode.ShouldBe(HttpStatusCode.Created, await workflow.Content.ReadAsStringAsync(ct));

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/v1/registry/default-workflow")
        {
            Content = JsonContent.Create(new { workflowId = WorkflowId, workflowVersion = "1" }),
        };
        put.Headers.Add("X-CSRF-Token", admin.AntiforgeryToken);
        using var made = await admin.Client.SendAsync(put, ct);
        made.IsSuccessStatusCode.ShouldBeTrue(await made.Content.ReadAsStringAsync(ct));
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    /// <summary>A compatibility entry for the engine under test, so the preflight passes on Docker Engine and Docker Desktop alike.</summary>
    private static async Task<string> WriteCompatibilityListAsync(DockerEngine docker, CancellationToken ct)
    {
        var version = await docker.GetJsonAsync("/version", ct);
        var platform = version.TryGetProperty("Platform", out var p) && p.TryGetProperty("Name", out var n) ? n.GetString() ?? "Docker" : "Docker";
        var product = platform.StartsWith("Docker Desktop", StringComparison.OrdinalIgnoreCase) ? "Docker Desktop"
            : platform.StartsWith("Docker Engine", StringComparison.OrdinalIgnoreCase) ? "Docker Engine" : platform.Trim();
        var info = await docker.GetJsonAsync("/info", ct);
        var entry = new[]
        {
            new
            {
                profile = "docker-desktop",
                runtimeProduct = product,
                runtimeVersionRange = "*",
                backend = info.GetProperty("OSType").GetString(),
                os = "linux",
                osVersionRange = "*",
                architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64",
                accelerators = Array.Empty<string>(),
                managerVersionRange = "*",
            },
        };
        var path = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-compat-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(entry), ct);
        return path;
    }

    private static async Task WaitOrDiagnoseAsync(EndToEndHarness harness, Guid runId, Func<Task<bool>> condition, string failure, CancellationToken ct)
    {
        try
        {
            await EndToEndHarness.WaitAsync(condition, TimeSpan.FromMinutes(3), failure, ct);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(failure + "\n" + await WorkExecutionEndToEndTests.DiagnosticsAsync(harness, runId, ct));
        }
    }

    private static async Task<Guid> SeedReadyJobAsync(EndToEndHarness harness, CancellationToken ct)
    {
        await using var scope = harness.Platform.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var resolution = await sp.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct);
        var definition = resolution.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;
        var mapped = sp.GetRequiredService<WorkflowSnapshotMapper>().Map(definition);
        var planned = LogicalJobPlanner.Plan(mapped.PlannerNodes, []);
        var nodes = definition.Nodes.Select((d, i) => new WorkflowNodeRecord(
            Guid.CreateVersion7(), d.NodeKey, d.CapabilityId, d.CapabilityVersion, d.Tier, d.ExecutionScope, i,
            WorkflowNodeState.Waiting, null, default, mapped.NodeSnapshotJson[d.NodeKey])).ToList();
        var byKey = nodes.ToDictionary(n => n.NodeKey, StringComparer.Ordinal);

        await using var uow = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        var runId = await sp.GetRequiredService<IAnalysisRunStore>().InsertAsync(new NewAnalysisRun(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, definition.WorkflowId, definition.WorkflowVersion, definition.RequestedCapabilities,
            definition.CompletionPolicy, AnalysisRunState.Requested, null, new AnalysisRunSnapshotRecord(mapped.RunSnapshotJson, mapped.RunSnapshotDigest),
            nodes, []), ct);
        await sp.GetRequiredService<ILogicalJobStore>().InsertAsync(planned.Select(p => new NewLogicalJob(
            Guid.CreateVersion7(), runId, byKey[p.NodeKey].Id, p.Scope, null, null, null, null, LogicalJobState.Waiting,
            new LogicalJobSnapshotRecord(
                p.Snapshot.MaxAttempts, p.Snapshot.LeaseSeconds, p.Snapshot.StaleTimeoutSeconds, p.Snapshot.ExecutionTimeoutSeconds,
                p.Snapshot.AnalystProfileDigest.ToString(), p.Snapshot.ImageDigest.ToString(), p.Snapshot.ModelDigest?.ToString(),
                JsonSerializer.Serialize(p.Snapshot.Resources), p.Snapshot.SchemaVersion))).ToList(), ct);
        await uow.CommitAsync(ct);

        await using var applyScope = harness.Platform.Services.CreateAsyncScope();
        await using var apply = await applyScope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await applyScope.ServiceProvider.GetRequiredService<IAnalysisRunStore>().LockRunAsync(runId, ct);
        await applyScope.ServiceProvider.GetRequiredService<RunReadinessCoordinator>().ApplyAsync(runId, ct);
        await apply.CommitAsync(ct);
        return runId;
    }
}

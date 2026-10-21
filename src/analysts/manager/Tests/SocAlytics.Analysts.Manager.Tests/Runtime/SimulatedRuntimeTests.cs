using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Tests.Work;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Runtime;

public sealed class SimulatedRuntimeTests
{
    private static readonly AttemptLaunch Launch = new(
        "attempt-1", "job-1", "cap-a", "sha256:cap", "scope-1", "sha256:profile", "sha256:image", "sha256:model",
        new AttemptResources(1, 1024, []));

    private static SimulatedRuntimeAdapter Adapter(FakeTimeProvider time, SimulatedCapabilityOptions behavior, int max = 4) =>
        new(new SimulatedAnalystOptions
        {
            MaxParallelAttempts = max,
            Capabilities = { ["cap-a"] = behavior },
        }, time);

    [Fact]
    public async Task Succeed_returns_configured_result_after_run_duration()
    {
        var time = new FakeTimeProvider();
        var adapter = Adapter(time, new SimulatedCapabilityOptions
        {
            RunDuration = TimeSpan.FromSeconds(30),
            ResultReference = "ref://r1",
            ResultChecksum = "sha256:abc",
        });
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        execution.Outcome.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromSeconds(30));
        var outcome = await execution.Outcome;
        outcome.Succeeded.ShouldBeTrue();
        outcome.ResultReference.ShouldBe("ref://r1");
        outcome.ResultChecksum.ShouldBe("sha256:abc");
    }

    [Fact]
    public async Task Unconfigured_capability_succeeds_immediately()
    {
        var adapter = new SimulatedRuntimeAdapter(new SimulatedAnalystOptions(), new FakeTimeProvider());
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        (await execution.Outcome).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Fail_returns_configured_reason()
    {
        var adapter = Adapter(new FakeTimeProvider(), new SimulatedCapabilityOptions
        {
            Outcome = SimulatedOutcome.Fail,
            FailureReason = "model-error",
        });
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        var outcome = await execution.Outcome;
        outcome.Succeeded.ShouldBeFalse();
        outcome.FailureReason.ShouldBe("model-error");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public async Task Fail_segment_starts_fail_only_the_listed_segment_windows(int segmentNumber, bool succeeds)
    {
        var adapter = Adapter(new FakeTimeProvider(), new SimulatedCapabilityOptions
        {
            FailureReason = "segment-error",
            FailSegmentStartsMs = [0],
        });
        var job = new global::SocAlytics.Analysts.Manager.Work.AnalystJobMessage(
            "m", "o", "run-1", "node-1", "job-1", "team-1", "match-1",
            new global::SocAlytics.Analysts.Manager.Platform.QueueCapability("cap-a", "1.0.0"), "1.0.0",
            "segment", null, new global::SocAlytics.Analysts.Manager.Work.JobPinned("d", "i", null),
            new global::SocAlytics.Analysts.Manager.Work.JobResources(1, 1),
            new global::SocAlytics.Analysts.Manager.Platform.CompletionCorrelation("c"),
            "rsv-1", new global::SocAlytics.Analysts.Manager.Work.JobSegment("rec-1", segmentNumber));
        var launch = Launch with { Job = job };

        var execution = await adapter.StartAttemptAsync(launch, TestContext.Current.CancellationToken);
        var outcome = await execution.Outcome;

        outcome.Succeeded.ShouldBe(succeeds);
        if (!succeeds)
        {
            outcome.FailureReason.ShouldBe("segment-error");
        }
    }

    [Fact]
    public async Task The_simulated_runtime_sees_the_job_and_attempt_files_of_a_1_0_0_job()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
        var attemptId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var occurrenceId = Guid.NewGuid();
        var platform = new FakePlatform(time);
        platform.Script(WorkOperation.Claim, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{attemptId}}","logicalJobId":"{{jobId}}","readinessOccurrenceId":"{{occurrenceId}}","attemptNumber":3,"fencingToken":7,"leaseExpiresAt":"{{time.GetUtcNow().AddSeconds(60):O}}","leaseSeconds":60,"staleTimeoutSeconds":120,"executionTimeoutSeconds":600,"replayed":false}"""));
        platform.Script(WorkOperation.Heartbeat, () => Json(HttpStatusCode.OK,
            $$"""{"attemptId":"{{attemptId}}","leaseExpiresAt":"{{time.GetUtcNow().AddSeconds(60):O}}"}"""));
        platform.Script(WorkOperation.Completion, () => Json(HttpStatusCode.Created,
            $$"""{"attemptId":"{{attemptId}}","logicalJobId":"{{jobId}}","outcome":"accepted","acceptedResultId":"{{Guid.NewGuid()}}","replayed":false,"logicalJobState":"succeeded"}"""));
        var payload = new JsonObject
        {
            ["contractVersion"] = "1.0.0",
            ["messageId"] = Guid.NewGuid().ToString(),
            ["readinessOccurrenceId"] = occurrenceId.ToString(),
            ["runId"] = "run-1",
            ["nodeId"] = "node-1",
            ["logicalJobId"] = jobId.ToString(),
            ["teamId"] = "team-1",
            ["matchId"] = "match-1",
            ["capability"] = new JsonObject { ["id"] = "player-detection", ["version"] = "1.0.0" },
            ["schemaVersion"] = "1.0.0",
            ["executionScope"] = "match",
            ["inputs"] = new JsonArray(),
            ["pinned"] = new JsonObject
            {
                ["analystProfileDigest"] = "sha-256:" + new string('3', 64),
                ["imageDigest"] = "sha-256:" + new string('4', 64),
            },
            ["resources"] = new JsonObject { ["cpuMillicores"] = 1000, ["memoryMebibytes"] = 2048 },
            ["correlation"] = new JsonObject { ["correlationId"] = "corr-1" },
        }.ToJsonString();
        var adapter = new SimulatedRuntimeAdapter(new SimulatedAnalystOptions
        {
            Capabilities = { ["player-detection"] = new SimulatedCapabilityOptions { RunDuration = TimeSpan.FromSeconds(30) } },
        }, time);
        var client = new PlatformClient(new HttpClient(platform), new ProofFactory(time));
        var session = new PlatformSession(new Uri("https://platform.test"), new InMemoryDeviceKeyProvider().ImportFixtureKey(), "opaque-token");
        var runner = new AttemptRunner(client, adapter, time);

        var task = runner.RunAsync(new InMemoryDelivery(platform, payload), () => session, null, TestContext.Current.CancellationToken);
        for (var i = 0; i < 5000 && adapter.Attempts.Count == 0; i++)
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        var launch = adapter.Attempts.Single().Launch;
        (await File.ReadAllBytesAsync(launch.JobFile!, TestContext.Current.CancellationToken)).ShouldBe(System.Text.Encoding.UTF8.GetBytes(payload));
        var attempt = JsonNode.Parse(await File.ReadAllTextAsync(launch.AttemptFile!, TestContext.Current.CancellationToken))!;
        attempt["contractVersion"]!.GetValue<string>().ShouldBe("1.0.0");
        attempt["attemptId"]!.GetValue<string>().ShouldBe(attemptId.ToString());
        attempt["attemptNumber"]!.GetValue<int>().ShouldBe(3);

        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        (await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ShouldBe(AttemptRunEnd.Completed);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Crash_faults_the_outcome_without_a_result()
    {
        var adapter = Adapter(new FakeTimeProvider(), new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Crash });
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        await Should.ThrowAsync<InvalidOperationException>(() => execution.Outcome);
    }

    [Fact]
    public async Task Hang_never_completes_until_stopped()
    {
        var time = new FakeTimeProvider();
        var adapter = Adapter(time, new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang });
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromDays(30));
        execution.Outcome.IsCompleted.ShouldBeFalse();

        await execution.StopAsync(TestContext.Current.CancellationToken);
        await Should.ThrowAsync<OperationCanceledException>(() => execution.Outcome);
        adapter.Attempts.Single().Stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task Clean_up_stops_and_marks_the_attempt_cleaned()
    {
        var adapter = Adapter(new FakeTimeProvider(), new SimulatedCapabilityOptions { Outcome = SimulatedOutcome.Hang });
        var execution = await adapter.StartAttemptAsync(Launch, TestContext.Current.CancellationToken);
        await execution.CleanUpAsync(TestContext.Current.CancellationToken);
        var attempt = adapter.Attempts.Single();
        attempt.CleanedUp.ShouldBeTrue();
        attempt.Stopped.ShouldBeTrue();
    }

    [Fact]
    public async Task Preflight_result_carries_max_parallel_attempts()
    {
        var adapter = Adapter(new FakeTimeProvider(), new SimulatedCapabilityOptions(), max: 3);
        var runner = new PreflightRunner(
            new RuntimeAdapterCatalog([adapter]),
            SimulatedRuntimeAdapter.List(),
            Options.Create(new RuntimeOptions { Profile = SimulatedRuntimeAdapter.TestProfile }),
            TimeProvider.System);
        var result = await runner.RunAsync(TestContext.Current.CancellationToken);
        result.Passed.ShouldBeTrue();
        result.MaxParallelAttempts.ShouldBe(3);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Test", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void Simulated_profile_is_available_only_in_development_and_test(string environment, bool allowed)
    {
        var builder = ManagerHost.CreateBuilder([$"--environment={environment}", "--Runtime:Profile=simulated"]);
        using var host = builder.Build();
        if (allowed)
        {
            host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find("simulated").ShouldNotBeNull();
        }
        else
        {
            Should.Throw<InvalidOperationException>(() => host.Services.GetRequiredService<RuntimeAdapterCatalog>())
                .Message.ShouldContain("Development and Test");
        }
    }

    [Fact]
    public void Production_without_simulated_profile_has_no_adapters()
    {
        var builder = ManagerHost.CreateBuilder(["--environment=Production"]);
        using var host = builder.Build();
        host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find("simulated").ShouldBeNull();
    }
}

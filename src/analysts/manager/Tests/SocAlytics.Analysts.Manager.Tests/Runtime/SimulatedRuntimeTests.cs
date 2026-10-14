using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
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

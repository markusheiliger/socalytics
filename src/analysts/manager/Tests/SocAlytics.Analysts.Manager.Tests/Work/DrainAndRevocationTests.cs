#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Work;

/// <summary>User Story 4 at the Manager level: drain policies and revocation with active attempts.</summary>
public sealed class DrainAndRevocationTests
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromMinutes(1);

    private static async Task<(ManagerTestHost Host, AttemptRegistry Registry)> StartAttempt(DrainTimeoutPolicy policy)
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Hang, runtimeMax: 2, stampMax: 2);
        try
        {
            await WorkAcquisitionTests.Restore(host);
            var service = await WorkAcquisitionTests.Prepare(host);
            var options = host.Services.GetRequiredService<IOptions<OperatingOptions>>().Value;
            options.DrainTimeout = DrainTimeout;
            options.DrainTimeoutPolicy = policy;
            WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
            (await service.StepAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
            var registry = host.Services.GetRequiredService<AttemptRegistry>();
            await WaitFor(() => registry.Attempts.Count == 1);
            return (host, registry);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
            await Task.Delay(10);
        }
    }

    private static List<JsonNode> Completions(ManagerTestHost host) => host.Platform.Requests
        .Where(r => r.Uri.AbsolutePath.EndsWith("/completion", StringComparison.Ordinal))
        .Select(r => JsonNode.Parse(r.Body)!)
        .ToList();

    [Theory]
    [InlineData(DrainPurpose.SafeExit)]
    [InlineData(DrainPurpose.Unregister)]
    public async Task Safe_exit_and_unregister_wait_for_the_active_attempt(DrainPurpose purpose)
    {
        var (host, registry) = await StartAttempt(DrainTimeoutPolicy.CancelRequest);
        await using var _ = host;
        var drain = host.Services.GetRequiredService<DrainCoordinator>().DrainAsync(purpose, TestContext.Current.CancellationToken);

        await Task.Delay(100);
        drain.IsCompleted.ShouldBeFalse();
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();

        await registry.StopAsync(AttemptStopReason.Exit, TestContext.Current.CancellationToken);

        (await drain.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(DrainResult.Completed);
        registry.ActiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancelRequest_cancels_the_drain_and_attempts_continue()
    {
        var (host, registry) = await StartAttempt(DrainTimeoutPolicy.CancelRequest);
        await using var _ = host;
        var drain = host.Services.GetRequiredService<DrainCoordinator>().DrainAsync(DrainPurpose.Unregister, TestContext.Current.CancellationToken);

        host.Time.Advance(DrainTimeout);

        (await drain.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(DrainResult.Cancelled);
        registry.ActiveCount.ShouldBe(1);
        Completions(host).ShouldBeEmpty();
        await registry.StopAsync(AttemptStopReason.Exit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopAndCleanUp_stops_remaining_attempts_and_reports_the_drain_timeout()
    {
        var (host, registry) = await StartAttempt(DrainTimeoutPolicy.StopAndCleanUp);
        await using var _ = host;
        var drain = host.Services.GetRequiredService<DrainCoordinator>().DrainAsync(DrainPurpose.SafeExit, TestContext.Current.CancellationToken);

        host.Time.Advance(DrainTimeout);

        (await drain.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBe(DrainResult.Completed);
        registry.ActiveCount.ShouldBe(0);
        var completion = Completions(host).ShouldHaveSingleItem();
        ((string)completion["outcome"]!).ShouldBe("failed");
        ((string)completion["failure"]!["category"]!).ShouldBe("cancelled-by-manager");
    }

    [Fact]
    public async Task Revocation_stops_everything_within_ten_seconds_and_sends_nothing_further()
    {
        var (host, registry) = await StartAttempt(DrainTimeoutPolicy.CancelRequest);
        await using var _ = host;
        var monitor = host.Services.GetRequiredService<RegistrationStatusMonitor>();
        var credentials = host.Services.GetRequiredService<QueueCredentialProvider>();
        credentials.Current.ShouldNotBeNull();
        host.Platform.Inactive = true;

        var watch = Stopwatch.StartNew();
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        registry.ActiveCount.ShouldBe(0);
        credentials.Current.ShouldBeNull();
        var sent = host.Platform.Requests.Count;
        host.Time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(200);
        host.Platform.Requests.Count.ShouldBe(sent);
        Completions(host).ShouldBeEmpty();
    }
}

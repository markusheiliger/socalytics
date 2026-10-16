#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Tests.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class QueueDepthPollingTests
{
    private static string Depth(DateTimeOffset at, int detection, int foreign) =>
        $$"""{"observedAt":"{{at:yyyy-MM-dd'T'HH:mm:ss'Z'}}","capabilities":[{"id":"ball-tracking","waiting":{{foreign}}},{"id":"player-detection","waiting":{{detection}}}],"total":{{detection + foreign}}}""";

    private static void ScriptDepth(ManagerTestHost host, int detection, int foreign) =>
        host.Platform.Script(WorkOperation.QueueDepth, () => WorkAcquisitionTests.Json(
            HttpStatusCode.OK, Depth(host.Time.GetUtcNow(), detection, foreign)));

    private static List<RecordedRequest> DepthRequests(ManagerTestHost host) =>
        host.Platform.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("/self/queue-depth", StringComparison.Ordinal)).ToList();

    private static async Task<(ManagerTestHost Host, RegistrationStatusMonitor Monitor)> Active()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, 2, 2);
        await WorkAcquisitionTests.Restore(host);
        await WorkAcquisitionTests.Prepare(host);
        return (host, host.Services.GetRequiredService<RegistrationStatusMonitor>());
    }

    [Fact]
    public async Task Polls_after_a_successful_check_with_the_assigned_capabilities()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 5, 1);

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        var request = DepthRequests(host).Single();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.Query.ShouldContain("capability=player-detection");
        var state = host.Services.GetRequiredService<QueueDepthState>().Snapshot();
        state.State.ShouldBe(QueueDepthStatus.Available);
        state.Total.ShouldBe(6);
        state.Capabilities.Single(c => c.Id == "player-detection").Waiting.ShouldBe(5);
    }

    [Fact]
    public async Task Does_not_poll_when_the_registration_check_fails()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 5, 1);
        host.Platform.UnreachableCount = 1;

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        DepthRequests(host).ShouldBeEmpty();
        host.Services.GetRequiredService<QueueDepthState>().Snapshot().State.ShouldBe(QueueDepthStatus.NotYetAvailable);
    }

    [Fact]
    public async Task Polls_in_paused_and_draining_states_too()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 2, 0);
        var machine = host.Services.GetRequiredService<RegistrationCoordinator>().Machine!;

        machine.PauseUntil(host.Time.GetUtcNow().AddHours(1)).ShouldBeTrue();
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        DepthRequests(host).Count.ShouldBe(1);

        machine.BeginDrain(DrainPurpose.SafeExit, host.Time.GetUtcNow().AddMinutes(5)).ShouldBeTrue();
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        DepthRequests(host).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Stops_and_clears_on_revocation()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 5, 1);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        var state = host.Services.GetRequiredService<QueueDepthState>();
        state.Snapshot().State.ShouldBe(QueueDepthStatus.Available);

        host.Platform.Inactive = true;
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        state.Snapshot().State.ShouldBe(QueueDepthStatus.NotYetAvailable);
        var polls = DepthRequests(host).Count;
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        DepthRequests(host).Count.ShouldBe(polls);
    }

    [Fact]
    public async Task Clears_when_a_local_unregistration_completes_without_a_status_check()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 5, 1);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        host.Control.GetStatus().QueueDepth.Snapshot.State.ShouldBe(QueueDepthStatus.Available);

        (await host.Control.UnregisterAsync()).Succeeded.ShouldBeTrue();

        var status = host.Control.GetStatus();
        status.RegistrationState.ShouldBe("unregistered");
        status.QueueDepth.Snapshot.State.ShouldBe(QueueDepthStatus.NotYetAvailable);
        status.QueueDepth.AgeSeconds.ShouldBeNull();
        host.Services.GetRequiredService<QueueDepthState>().Snapshot().State.ShouldBe(QueueDepthStatus.NotYetAvailable);
    }

    [Fact]
    public async Task Failed_request_marks_the_observation_stale()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 5, 1);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        host.Platform.Script(WorkOperation.QueueDepth, HttpStatusCode.ServiceUnavailable, "{}");
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        var snapshot = host.Services.GetRequiredService<QueueDepthState>().Snapshot();
        snapshot.State.ShouldBe(QueueDepthStatus.Stale);
        snapshot.Total.ShouldBe(6);
    }

    [Fact]
    public async Task No_assignment_is_no_capability_and_sends_no_request()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, 2, 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var monitor = host.Services.GetRequiredService<RegistrationStatusMonitor>();

        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        DepthRequests(host).ShouldBeEmpty();
        host.Services.GetRequiredService<QueueDepthState>().Snapshot().State.ShouldBe(QueueDepthStatus.NoCapability);
    }

    [Fact]
    public async Task New_credentials_with_a_different_capability_set_change_the_next_request()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        ScriptDepth(host, 1, 1);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        DepthRequests(host)[^1].Uri.Query.ShouldContain("capability=ball-tracking");

        host.Platform.Script(WorkOperation.QueueCredentials, () => WorkAcquisitionTests.Json(HttpStatusCode.Created,
            $$"""{"userJwt":"jwt","expiresAt":"{{host.Time.GetUtcNow().AddHours(1):O}}","queueUrl":"tls://queue.test:4222","stream":"SOCALYTICS_ANALYSIS_JOBS","consumers":[{"capability":"player-detection","consumer":"analyst-player-detection"}],"inboxPrefix":"_INBOX_abc","maxConcurrentAnalysts":2}"""));
        host.Time.Advance(TimeSpan.FromMinutes(50));
        await WorkAcquisitionTests.Prepare(host);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        var query = DepthRequests(host)[^1].Uri.Query;
        query.ShouldContain("capability=player-detection");
        query.ShouldNotContain("ball-tracking");
    }

    [Fact]
    public async Task Jobs_ready_after_an_observation_appear_within_one_status_check_interval()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        var state = host.Services.GetRequiredService<QueueDepthState>();
        ScriptDepth(host, 0, 0);
        var delay = await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        state.Snapshot().Total.ShouldBe(0);

        ScriptDepth(host, 4, 0);
        host.Time.Advance(delay);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        state.Snapshot().Total.ShouldBe(4);
    }

    [Fact]
    public async Task Admission_is_identical_with_and_without_observations()
    {
        var (host, monitor) = await Active();
        await using var _ = host;
        var gate = host.Services.GetRequiredService<AdmissionGate>();
        var before = gate.TryAdmit();

        ScriptDepth(host, 100, 100);
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);
        var withObservation = gate.TryAdmit();
        host.Platform.Script(WorkOperation.QueueDepth, HttpStatusCode.ServiceUnavailable, "{}");
        await monitor.CheckOnceAsync(TestContext.Current.CancellationToken);

        withObservation.ShouldBe(before);
        gate.TryAdmit().ShouldBe(before);
    }
}

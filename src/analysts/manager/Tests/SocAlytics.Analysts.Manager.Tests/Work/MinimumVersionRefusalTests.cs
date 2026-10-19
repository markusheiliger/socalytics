#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Simulated;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.Work;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Work;

public sealed class MinimumVersionRefusalTests
{
    private const string Minimum = "99.0.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpResponseMessage VersionProblem() => WorkAcquisitionTests.Json(HttpStatusCode.Forbidden,
        $$"""{"type":"about:blank","title":"The Analyst Manager version is below the stamp's minimum.","status":403,"code":"analyst-manager-version-below-minimum","correlationId":"corr-1","minimumManagerVersion":"{{Minimum}}"}""");

    private static int Count(ManagerTestHost host, WorkOperation operation) =>
        host.Platform.Headers.Count(h => h.Operation == operation);

    private static MinimumVersionState State(ManagerTestHost host) => host.Services.GetRequiredService<MinimumVersionState>();

    private static AttemptRegistry Registry(ManagerTestHost host) => host.Services.GetRequiredService<AttemptRegistry>();

    private static async Task StepCredentialsAsync(ManagerTestHost host)
    {
        var sessions = host.Services.GetRequiredService<WorkSessionProvider>();
        var adapter = host.Services.GetRequiredService<RuntimeAdapterCatalog>().Find(SimulatedRuntimeAdapter.TestProfile)!;
        await sessions.RefreshAsync(Ct);
        await host.Services.GetRequiredService<QueueCredentialProvider>().StepAsync(
            () => host.Services.GetRequiredService<AdmissionGate>().TryAdmit() ? sessions.Current : null, () => adapter.Capabilities, Ct);
    }

    private static async Task CheckStatusAsync(ManagerTestHost host)
    {
        host.Time.Advance(TimeSpan.FromMinutes(2));
        await host.Services.GetRequiredService<RegistrationStatusMonitor>().CheckOnceAsync(Ct);
    }

    [Fact]
    public async Task A_refused_claim_negatively_acknowledges_the_delivery_and_stops_admission()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        var service = await WorkAcquisitionTests.Prepare(host);
        host.Platform.Script(WorkOperation.Claim, VersionProblem);
        var delivery = new InMemoryDelivery(host.Platform, WorkAcquisitionTests.Payload(0));
        queue.Ready.Enqueue(delivery);
        State(host).IsBelow.ShouldBeFalse();

        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        delivery.PlainNaks.ShouldBe(1);
        delivery.Acks.ShouldBe(0);
        State(host).RefusedByPlatform.ShouldBeTrue();
        State(host).IsBelow.ShouldBeTrue();
        State(host).Minimum.ShouldBe(Minimum);
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();

        var fetches = queue.Fetches.Count;
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
        (await service.StepAsync(Ct)).ShouldBeFalse();
        queue.Fetches.Count.ShouldBe(fetches);
        Count(host, WorkOperation.Claim).ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_credential_request_stops_further_requests()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        host.Platform.Script(WorkOperation.QueueCredentials, VersionProblem);

        await StepCredentialsAsync(host);

        Count(host, WorkOperation.QueueCredentials).ShouldBe(1);
        State(host).RefusedByPlatform.ShouldBeTrue();
        State(host).Minimum.ShouldBe(Minimum);
        host.Services.GetRequiredService<QueueCredentialProvider>().Current.ShouldBeNull();

        await StepCredentialsAsync(host);
        await StepCredentialsAsync(host);

        Count(host, WorkOperation.QueueCredentials).ShouldBe(1);
    }

    [Fact]
    public async Task The_next_status_response_with_a_satisfied_minimum_restores_credentials_and_claims()
    {
        var (host, queue) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        State(host).MarkRefused(Minimum);
        await StepCredentialsAsync(host);
        Count(host, WorkOperation.QueueCredentials).ShouldBe(0);
        var service = host.Services.GetRequiredService<WorkAcquisitionService>();
        WorkAcquisitionTests.Enqueue(queue, host.Platform, 1);
        (await service.StepAsync(Ct)).ShouldBeFalse();

        await CheckStatusAsync(host);

        State(host).RefusedByPlatform.ShouldBeFalse();
        State(host).IsBelow.ShouldBeFalse();
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeTrue();

        await StepCredentialsAsync(host);
        Count(host, WorkOperation.QueueCredentials).ShouldBe(1);
        (await service.StepAsync(Ct)).ShouldBeTrue();
        await Registry(host).WaitForIdleAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Count(host, WorkOperation.Claim).ShouldBe(1);
    }

    [Fact]
    public async Task A_status_response_that_still_lists_a_higher_minimum_keeps_the_refusal_in_effect()
    {
        var (host, _) = WorkAcquisitionTests.Host(SimulatedOutcome.Succeed, runtimeMax: 2, stampMax: 2);
        await using var _ = host;
        await WorkAcquisitionTests.Restore(host);
        State(host).MarkRefused(Minimum);

        State(host).Update(Minimum);

        State(host).RefusedByPlatform.ShouldBeFalse();
        State(host).IsBelow.ShouldBeTrue();
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
    }
}

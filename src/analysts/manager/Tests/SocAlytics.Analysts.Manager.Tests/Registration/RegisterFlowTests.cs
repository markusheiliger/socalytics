using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Hosting;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using System.Text.Json;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Registration;

public sealed class RegisterFlowTests
{
    private static readonly RegisterRequest Request = new("https://platform.test");

    private static async Task Settle(ManagerTestHost host, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static string State(ManagerTestHost host) => host.Control.GetStatus().RegistrationState;

    [Fact]
    public async Task FullFlowReachesActiveShowingCodeFingerprintAndUri()
    {
        await using var host = new ManagerTestHost();
        var result = await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeTrue();
        result.PairingCode.ShouldBe(FakePlatform.PairingCode);
        result.DeviceFingerprint.ShouldBe(host.Platform.Fingerprint);
        result.VerificationUri.ShouldBe("https://platform.test/registrar");
        host.Platform.ClaimedKeyProtection.ShouldBe("tpm");
        State(host).ShouldBe("pairing");

        await Settle(host, () => State(host) == "active");
        State(host).ShouldBe("active");
        host.Platform.Activated.ShouldBeTrue();
        var coordinator = host.Services.GetRequiredService<RegistrationCoordinator>();
        coordinator.Active!.ManagerId.ShouldBe("manager-1");
        coordinator.Active.Protection.ShouldBe(KeyProtection.Tpm);
        host.Platform.Requests.ShouldAllBe(r => r.Uri.Scheme == "https");
        host.Platform.Requests.ShouldNotContain(r => r.Body.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FingerprintMismatchAbortsAndDeletesKey()
    {
        await using var host = new ManagerTestHost();
        host.Platform.CorruptFingerprint = true;
        var result = await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);

        result.Succeeded.ShouldBeFalse();
        result.Code.ShouldBe(ManagerDiagnosticCodes.PairingRefused);
        host.Keys.CreatedCount.ShouldBe(1);
        host.Keys.LiveCount.ShouldBe(0);
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task SoftwareOnlyHostIsRefusedBeforeKeyCreationUnlessAllowed()
    {
        await using var refused = new ManagerTestHost(KeyProtection.Software);
        var result = await refused.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);
        result.Code.ShouldBe(ManagerDiagnosticCodes.SoftwareKeyNotAllowed);
        refused.Keys.CreatedCount.ShouldBe(0);

        await using var allowed = new ManagerTestHost(KeyProtection.Software);
        allowed.Platform.SoftwareKeysAllowed = true;
        (await allowed.Control.RegisterAsync(Request, TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();
        allowed.Platform.ClaimedKeyProtection.ShouldBe("software");
    }

    [Fact]
    public async Task NoProviderRefusesAndCreatesNothing()
    {
        await using var host = new ManagerTestHost(providerAvailable: false);
        var result = await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);
        result.Code.ShouldBe(ManagerDiagnosticCodes.NoSupportedKeyStore);
        host.Keys.CreatedCount.ShouldBe(0);
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task RejectionDeletesKeyAndReturnsToUnregistered()
    {
        await using var host = new ManagerTestHost();
        host.Platform.TerminalStatus = "rejected";
        (await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();
        await Settle(host, () => State(host) == "unregistered");
        State(host).ShouldBe("unregistered");
        host.Keys.LiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExpiryDeletesKeyAndReturnsToUnregistered()
    {
        await using var host = new ManagerTestHost();
        host.Platform.PendingPolls = int.MaxValue;
        (await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken)).Succeeded.ShouldBeTrue();
        host.Time.Advance(TimeSpan.FromMinutes(11));
        await Settle(host, () => State(host) == "unregistered");
        State(host).ShouldBe("unregistered");
        host.Keys.LiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task CancelDeletesKeyAndReturnsToUnregistered()
    {
        await using var host = new ManagerTestHost();
        host.Platform.PendingPolls = int.MaxValue;
        await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);
        await host.Control.CancelRegistrationAsync();
        State(host).ShouldBe("unregistered");
        host.Keys.LiveCount.ShouldBe(0);
    }

    [Fact]
    public async Task HttpEndpointAndStampMismatchAreRefused()
    {
        await using var host = new ManagerTestHost();
        (await host.Control.RegisterAsync(new RegisterRequest("http://platform.test"), TestContext.Current.CancellationToken))
            .Code.ShouldBe(ManagerDiagnosticCodes.InsecureEndpoint);
        host.Platform.Requests.ShouldBeEmpty();

        (await host.Control.RegisterAsync(Request with { StampId = "other" }, TestContext.Current.CancellationToken))
            .Code.ShouldBe(ManagerDiagnosticCodes.StampMismatch);
        host.Keys.CreatedCount.ShouldBe(0);
        State(host).ShouldBe("unregistered");
    }

    [Fact]
    public async Task LogsAndSnapshotNeverContainCodeOrHandle()
    {
        await using var host = new ManagerTestHost();
        await host.Control.RegisterAsync(Request, TestContext.Current.CancellationToken);
        var duringPairing = JsonSerializer.Serialize(host.Control.GetStatus());
        await Settle(host, () => State(host) == "active");
        var snapshot = duringPairing + JsonSerializer.Serialize(host.Control.GetStatus());
        var logs = string.Join('\n', host.Logs.GetSnapshot().Select(r => r.Message));

        foreach (var secret in new[] { FakePlatform.PairingCode, FakePlatform.PollingHandle, "challenge-" })
        {
            snapshot.ShouldNotContain(secret);
            logs.ShouldNotContain(secret);
        }
    }
}

using System.Text;
using Shouldly;
using Microsoft.Extensions.DependencyInjection;
using SocAlytics.Analysts.Manager.DeviceKeys;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Tests.Support;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.LocalState;

public sealed class SignedStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "socalytics-state-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryDeviceKeyProvider _keys = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private SignedStateStore Store(InMemoryDeviceKeyProvider? provider = null) => new(_dir, [provider ?? _keys]);

    private static ProtectedRegistrationState State(IDeviceKey key, string endpoint = "https://platform.test", int version = 1) =>
        new(version, "m1", "s1", endpoint, key.Reference, key.Protection, key.Thumbprint, OperatingIntent.Running, false,
            new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));

    private async Task<IDeviceKey> NewKey() => await _keys.CreateKeyAsync(true, TestContext.Current.CancellationToken);

    private static string Mutate(string jws, int part, Func<string, string> edit)
    {
        var p = jws.Split('.');
        p[part] = edit(p[part]);
        return string.Join('.', p);
    }

    [Fact]
    public async Task RoundTripAndAtomicReplace()
    {
        var key = await NewKey();
        var store = Store();
        store.Write(State(key), key);
        store.Write(State(key), key);
        var read = await store.ReadAsync(TestContext.Current.CancellationToken);
        read.Succeeded.ShouldBeTrue();
        read.State!.ManagerId.ShouldBe("m1");
        read.State.Intent.ShouldBe(OperatingIntent.Running);
        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe([SignedStateStore.FileName]);
        var text = File.ReadAllText(store.Path);
        var decoded = Encoding.UTF8.GetString(PublicJwk.FromBase64Url(text.Split('.')[1]));
        foreach (var forbidden in new[] { "token", "assertion", "proof", "pairing", "polling", "challenge", "\"d\"" })
        {
            decoded.ShouldNotContain(forbidden, Case.Insensitive);
        }

        store.Delete();
        File.Exists(store.Path).ShouldBeFalse();
    }

    [Fact]
    public async Task MissingFile() =>
        (await Store().ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateMissing);

    [Fact]
    public async Task EditedPayloadAndSwappedSignatureFailSignature()
    {
        var key = await NewKey();
        var store = Store();
        store.Write(State(key), key);
        var good = File.ReadAllText(store.Path);
        var edited = Mutate(good, 1, p => PublicJwk.Base64Url(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(PublicJwk.FromBase64Url(p)).Replace("m1", "m2"))));
        File.WriteAllText(store.Path, edited);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateSignature);

        var other = await NewKey();
        var otherStore = Store();
        otherStore.Write(State(other), other);
        var otherSig = File.ReadAllText(otherStore.Path).Split('.')[2];
        File.WriteAllText(store.Path, Mutate(good, 2, _ => otherSig));
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateSignature);
    }

    [Fact]
    public async Task CopySignedByAnotherKeyFailsSignature()
    {
        var key = await NewKey();
        var other = await NewKey();
        var store = Store();
        store.Write(State(key), other);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateSignature);
    }

    [Fact]
    public async Task UnknownVersionAndHttpEndpointAreUnreadable()
    {
        var key = await NewKey();
        var store = Store();
        store.Write(State(key, version: 2), key);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateUnreadable);
        store.Write(State(key, "http://platform.test"), key);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StateUnreadable);
    }

    [Fact]
    public async Task MissingKey()
    {
        var key = await NewKey();
        var store = Store();
        store.Write(State(key), key);
        await _keys.DeleteKeyAsync(key.Reference, TestContext.Current.CancellationToken);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.KeyMissing);
    }

    [Fact]
    public async Task GroupOrWorldReadableFailsPermissions()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var key = await NewKey();
        var store = Store();
        store.Write(State(key), key);
        File.SetUnixFileMode(store.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        (await store.ReadAsync(TestContext.Current.CancellationToken)).Code.ShouldBe(ManagerDiagnosticCodes.StatePermissions);
    }

    [Fact]
    public async Task ActivationWritesStateAndLeavesGateClosed()
    {
        await using var host = new ManagerTestHost();
        await host.Control.RegisterAsync(new("https://platform.test"), TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && host.Control.GetStatus().RegistrationState != "active"; i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        var status = host.Control.GetStatus();
        status.RegistrationState.ShouldBe("active");
        status.OperatingState.ShouldBe("runtime-unavailable");
        status.OperatingIntent.ShouldBe("running");
        host.Services.GetRequiredService<AdmissionGate>().TryAdmit().ShouldBeFalse();
        var read = await host.Services.GetRequiredService<SignedStateStore>().ReadAsync(TestContext.Current.CancellationToken);
        read.Succeeded.ShouldBeTrue();
        read.State!.ManagerId.ShouldBe("manager-1");
    }
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Analysts.Manager.LocalState;
using SocAlytics.Analysts.Manager.Operating;
using SocAlytics.Analysts.Manager.Registration;
using SocAlytics.Analysts.Manager.Tests.Support;
using SocAlytics.Analysts.Manager.ViewModels;
using Xunit;

namespace SocAlytics.Analysts.Manager.Tests.Secrets;

public sealed partial class SecretAbsenceTests
{
    private const string Pin = "pin-7f3a9c1e5b2d4086";

    [GeneratedRegex(@"[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex CompactJwsPattern();

    private static string State(ManagerTestHost h) => h.Control.GetStatus().RegistrationState;

    private static async Task Settle(ManagerTestHost host, Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            host.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private static List<string> Secrets(ManagerTestHost host)
    {
        var secrets = new List<string> { FakePlatform.PairingCode, FakePlatform.PollingHandle, "challenge-1", "opaque-token", Pin };
        foreach (var request in host.Platform.Requests.ToList())
        {
            secrets.AddRange(CompactJwsPattern().Matches(request.Body).Select(m => m.Value));
        }

        return secrets;
    }

    private static void AssertAbsent(ManagerTestHost host, string where, string text)
    {
        foreach (var secret in Secrets(host))
        {
            text.Contains(secret, StringComparison.Ordinal).ShouldBeFalse($"{where} leaks a secret ({secret.Length} chars)");
        }
    }

    private static string Render(object view) =>
        string.Join('\n', view.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0)
            .Select(p => p.GetValue(view) switch
            {
                null => string.Empty,
                string s => s,
                var o => o.GetType().IsPrimitive ? o.ToString()! : o.GetType().Name,
            }));

    private static void Inspect(ManagerTestHost host)
    {
        AssertAbsent(host, "status snapshot", JsonSerializer.Serialize(host.Control.GetStatus()));
        AssertAbsent(host, "status window", Render(new StatusWindowViewModel(host.Control)));
        AssertAbsent(host, "tray menu", Render(new TrayMenuViewModel(host.Control)));
        AssertAbsent(host, "log output", string.Join('\n', host.Logs.GetSnapshot().Select(l => l.Message + " " + l.Exception)));
        foreach (var file in Directory.EnumerateFiles(host.StateDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) != "pkcs11.pin")
            {
                AssertAbsent(host, "state file " + Path.GetFileName(file), Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            }
        }
    }

    private static void ProvisionPin(ManagerTestHost host)
    {
        Directory.CreateDirectory(host.StateDirectory);
        File.WriteAllText(Path.Combine(host.StateDirectory, "pkcs11.pin"), Pin);
    }

    [Fact]
    public async Task RegisterPauseUnregisterLeakNoSecrets()
    {
        await using var host = new ManagerTestHost();
        ProvisionPin(host);
        var ct = TestContext.Current.CancellationToken;

        var result = await host.Control.RegisterAsync(new RegisterRequest("https://platform.test"), ct);
        result.PairingCode.ShouldBe(FakePlatform.PairingCode);
        var shown = JsonSerializer.Serialize(result).Replace(FakePlatform.PairingCode, "<code>", StringComparison.Ordinal);
        AssertAbsent(host, "register result", shown);
        Inspect(host);

        await Settle(host, () => State(host) == "active");
        State(host).ShouldBe("active");
        Inspect(host);

        await host.Services.GetRequiredService<RegistrationStatusMonitor>().CheckOnceAsync(ct);
        await host.Control.PauseAsync(TimeSpan.FromMinutes(30));
        Inspect(host);
        await host.Control.ResumeAsync();
        Inspect(host);

        (await host.Control.UnregisterAsync()).Succeeded.ShouldBeTrue();
        Inspect(host);
    }

    [Fact]
    public async Task RestoreAndRevocationLeakNoSecrets()
    {
        await using var host = new ManagerTestHost();
        ProvisionPin(host);
        var ct = TestContext.Current.CancellationToken;
        var key = await host.Keys.CreateKeyAsync(false, ct);
        host.Platform.Adopt(key.PublicJwk);
        host.Services.GetRequiredService<SignedStateStore>().Write(
            new ProtectedRegistrationState(
                1, "manager-1", "stamp-1", "https://platform.test", key.Reference, key.Protection, key.Thumbprint,
                OperatingIntent.Running, false, host.Time.GetUtcNow()),
            key);
        await host.Services.GetRequiredService<RegistrationRestorer>().RunAsync(ct);
        State(host).ShouldBe("active");
        var monitor = host.Services.GetRequiredService<RegistrationStatusMonitor>();
        await monitor.CheckOnceAsync(ct);
        Inspect(host);

        host.Platform.Inactive = true;
        await monitor.CheckOnceAsync(ct);
        State(host).ShouldBe("unregistered");
        Inspect(host);
    }
}

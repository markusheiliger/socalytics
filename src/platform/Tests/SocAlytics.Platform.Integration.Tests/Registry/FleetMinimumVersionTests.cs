using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class FleetMinimumVersionTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string StatusPath = "/api/v1/analyst-managers/self/status";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";

    // The pairing version of every registration in these tests is 1.0.0 (RegistryTestHost.PairingBody).
    private const string Minimum = "1.1.0";

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin, ApiSession Registrar, TestAnalystManager Manager, string ClientId, Guid Id, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Registrar.Dispose();
            Anon.Dispose();
            Manager.Dispose();
            await Factory.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private async Task<Setup> StartAsync(string? minimum, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = new PlatformApiFactory(db, new Dictionary<string, string?>
        {
            ["ClubDisplayName"] = "Registry Club",
            ["FirstClubAdmin:AccountName"] = "registry-admin",
            ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
        }, configureServices: services => services.Configure<RegistryOptions>(o => o.RateLimits.StatusPerMinute = 10_000),
        extraSettings: minimum is null ? null : new Dictionary<string, string?> { ["Registry:ManagerVersion:Minimum"] = minimum });
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var registrar = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        var (id, handle) = await SubmitAsync(registrar, anon, manager, ct);
        using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadAsync(ok, ct)).TryGetProperty("minimumManagerVersion", out _).ShouldBeFalse();
        using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var act = await anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
        var s = new Setup(db, factory, anon, admin, registrar, manager, clientId, id, "");
        return s with { Token = await TokenAsync(s, ct) };
    }

    private static async Task<(Guid Id, string Handle)> SubmitAsync(ApiSession member, HttpClient anon, TestAnalystManager manager, CancellationToken ct)
    {
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        pair.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await member.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        sub.StatusCode.ShouldBe(HttpStatusCode.Created, await sub.Content.ReadAsStringAsync(ct));
        var submitted = await ReadAsync(sub, ct);
        submitted.TryGetProperty("minimumManagerVersion", out _).ShouldBeFalse();
        submitted.TryGetProperty("belowMinimumVersion", out _).ShouldBeFalse();
        return (submitted.GetProperty("registrationId").GetGuid(), handle);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    private static async Task<string> TokenAsync(Setup s, CancellationToken ct)
    {
        var now = s.Factory.Time.GetUtcNow();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = s.Manager.ClientAssertion(now, Origin + TokenPath, s.ClientId),
                ["client_id"] = s.ClientId,
            }),
        };
        request.Headers.Add("DPoP", s.Manager.DPoPProof(now, "POST", Origin + TokenPath));
        using var r = await s.Anon.SendAsync(request, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
    }

    private static async Task ReportAsync(Setup s, string version, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StatusPath)
        {
            Content = JsonContent.Create(new
            {
                operatingState = "running",
                pausedUntil = (string?)null,
                runtimeUnavailableReason = (string?)null,
                activeAnalysts = 1,
                maxAnalysts = 2,
                managerVersion = version,
            }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + StatusPath, s.Token));
        using var r = await s.Anon.SendAsync(request, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
    }

    // The detail and the list item of the registration, which must agree.
    private static async Task<JsonElement[]> EntriesAsync(Setup s, Guid id, CancellationToken ct)
    {
        using var detail = await s.Admin.SendAsync(HttpMethod.Get, $"{Path}/{id}", null, ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK, await detail.Content.ReadAsStringAsync(ct));
        using var list = await s.Admin.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var item = (await ReadAsync(list, ct)).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("registrationId").GetGuid() == id);
        return [await ReadAsync(detail, ct), item];
    }

    private static void ShouldBeMarked(JsonElement[] entries, string? minimum, bool below)
    {
        foreach (var e in entries)
        {
            if (minimum is null)
            {
                e.GetProperty("minimumManagerVersion").ValueKind.ShouldBe(JsonValueKind.Null);
            }
            else
            {
                e.GetProperty("minimumManagerVersion").GetString().ShouldBe(minimum);
            }

            e.GetProperty("belowMinimumVersion").GetBoolean().ShouldBe(below);
        }
    }

    [Fact]
    public async Task Active_manager_that_never_reported_is_judged_by_its_pairing_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(Minimum, ct);
        var entries = await EntriesAsync(s, s.Id, ct);
        entries[0].GetProperty("state").GetString().ShouldBe("active");
        entries[0].GetProperty("connectivity").GetString().ShouldBe("offline");
        ShouldBeMarked(entries, Minimum, below: true);
    }

    [Fact]
    public async Task Latest_report_replaces_the_pairing_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(Minimum, ct);

        await ReportAsync(s, "1.1.0", ct);
        var entries = await EntriesAsync(s, s.Id, ct);
        entries[0].GetProperty("connectivity").GetString().ShouldBe("online");
        ShouldBeMarked(entries, Minimum, below: false);

        await ReportAsync(s, "1.0.9", ct);
        ShouldBeMarked(await EntriesAsync(s, s.Id, ct), Minimum, below: true);
    }

    [Fact]
    public async Task Offline_manager_is_marked_by_its_last_reported_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(Minimum, ct);
        await ReportAsync(s, "0.3.0", ct);
        s.Factory.Time.Advance(TimeSpan.FromSeconds(181));

        var entries = await EntriesAsync(s, s.Id, ct);
        entries[0].GetProperty("connectivity").GetString().ShouldBe("offline");
        ShouldBeMarked(entries, Minimum, below: true);
    }

    [Fact]
    public async Task Pending_registration_is_judged_by_its_pairing_version()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(Minimum, ct);
        using var other = new TestAnalystManager();
        var (id, _) = await SubmitAsync(s.Registrar, s.Anon, other, ct);

        var entries = await EntriesAsync(s, id, ct);
        entries[0].GetProperty("state").GetString().ShouldBe("pending_approval");
        ShouldBeMarked(entries, Minimum, below: true);
    }

    [Fact]
    public async Task Without_a_minimum_nothing_is_marked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(null, ct);
        using var other = new TestAnalystManager();
        var (pending, _) = await SubmitAsync(s.Registrar, s.Anon, other, ct);

        ShouldBeMarked(await EntriesAsync(s, s.Id, ct), null, below: false);
        ShouldBeMarked(await EntriesAsync(s, pending, ct), null, below: false);
    }

    [Fact]
    public async Task Registrar_view_and_decision_responses_carry_neither_property()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(Minimum, ct);
        using var other = new TestAnalystManager();
        var (pending, _) = await SubmitAsync(s.Registrar, s.Anon, other, ct);

        using var list = await s.Registrar.SendAsync(HttpMethod.Get, Path, null, ct);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await ReadAsync(list, ct)).GetProperty("items").EnumerateArray().ToArray();
        items.Length.ShouldBeGreaterThanOrEqualTo(2);
        foreach (var item in items)
        {
            item.TryGetProperty("minimumManagerVersion", out _).ShouldBeFalse();
            item.TryGetProperty("belowMinimumVersion", out _).ShouldBeFalse();
        }

        using var detail = await s.Registrar.SendAsync(HttpMethod.Get, $"{Path}/{pending}", null, ct);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        var d = await ReadAsync(detail, ct);
        d.TryGetProperty("minimumManagerVersion", out _).ShouldBeFalse();
        d.TryGetProperty("belowMinimumVersion", out _).ShouldBeFalse();

        using var reject = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{pending}/rejection", new { reason = "unknown device" }, ct);
        reject.StatusCode.ShouldBe(HttpStatusCode.OK, await reject.Content.ReadAsStringAsync(ct));
        var rejected = await ReadAsync(reject, ct);
        rejected.TryGetProperty("minimumManagerVersion", out _).ShouldBeFalse();
        rejected.TryGetProperty("belowMinimumVersion", out _).ShouldBeFalse();
    }
}

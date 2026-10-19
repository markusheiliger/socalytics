using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class StatusMinimumVersionTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string StatusPath = "/api/v1/analyst-managers/self/status";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";
    private const string MinimumKey = "Registry:ManagerVersion:Minimum";

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(System.IO.Path.Combine(
        AppContext.BaseDirectory, "contracts", "analyst-manager", "status", "v1", "status.schema.json"))));

    private sealed record Setup(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin, TestAnalystManager Manager, string ClientId, Guid Id, string Token) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
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
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var act = await anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;
        var s = new Setup(db, factory, anon, admin, manager, clientId, id, "");
        return s with { Token = await TokenAsync(s, ct) };
    }

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

    private static Task<HttpResponseMessage> ReportAsync(Setup s, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, StatusPath) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + StatusPath, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static object Report() => new
    {
        operatingState = "running",
        pausedUntil = (string?)null,
        runtimeUnavailableReason = (string?)null,
        activeAnalysts = 1,
        maxAnalysts = 2,
        managerVersion = "0.3.0",
    };

    private static async Task<JsonElement> ReportedAsync(Setup s, object body, CancellationToken ct)
    {
        using var r = await ReportAsync(s, body, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        Schema.Value.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid.ShouldBeTrue(root.GetRawText());
        return root;
    }

    private static void SetMinimum(Setup s, string? value)
    {
        var root = (IConfigurationRoot)s.Factory.Services.GetRequiredService<IConfiguration>();
        root[MinimumKey] = value;
        try
        {
            root.Reload();
        }
        catch (Exception ex) when (ex is OptionsValidationException || ex is AggregateException { InnerException: OptionsValidationException })
        {
            // The options monitor revalidates inside the change callback; the provider keeps the last valid minimum.
        }
    }

    [Fact]
    public async Task Response_carries_the_configured_minimum()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync("0.2.0", ct);
        var root = await ReportedAsync(s, Report(), ct);
        root.GetProperty("minimumManagerVersion").GetString().ShouldBe("0.2.0");
    }

    [Fact]
    public async Task Response_carries_null_without_a_minimum()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(null, ct);
        var root = await ReportedAsync(s, Report(), ct);
        root.GetProperty("minimumManagerVersion").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Rejected_report_still_carries_the_minimum()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync("0.2.0", ct);
        var root = await ReportedAsync(s, new { operatingState = "bogus", managerVersion = "0.3.0" }, ct);
        root.GetProperty("reportAccepted").GetBoolean().ShouldBeFalse();
        root.GetProperty("minimumManagerVersion").GetString().ShouldBe("0.2.0");
    }

    [Fact]
    public async Task Changed_minimum_appears_in_the_next_response_and_invalid_reload_keeps_the_last_valid_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync("0.2.0", ct);
        (await ReportedAsync(s, Report(), ct)).GetProperty("minimumManagerVersion").GetString().ShouldBe("0.2.0");

        SetMinimum(s, "0.4.1");
        (await ReportedAsync(s, Report(), ct)).GetProperty("minimumManagerVersion").GetString().ShouldBe("0.4.1");

        SetMinimum(s, "0.2");
        (await ReportedAsync(s, Report(), ct)).GetProperty("minimumManagerVersion").GetString().ShouldBe("0.4.1");

        SetMinimum(s, "");
        (await ReportedAsync(s, Report(), ct)).GetProperty("minimumManagerVersion").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}

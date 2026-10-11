using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class ManagerAuthenticationTests(PostgresContainerFixture postgres)
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string SelfPath = "/api/v1/analyst-managers/self/registration";
    private const string Origin = "https://localhost";

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

    private async Task<Setup> StartAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = RegistryTestHost.Create(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var manager = new TestAnalystManager();
        var anon = factory.CreateApiClient();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, "/api/v1/analyst-manager-registrations",
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"/api/v1/analyst-manager-registrations/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = manager.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var act = await anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        act.StatusCode.ShouldBe(HttpStatusCode.OK);
        var clientId = JsonDocument.Parse(await act.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;

        var now = factory.Time.GetUtcNow();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = manager.ClientAssertion(now, Origin + TokenPath, clientId),
                ["client_id"] = clientId,
            }),
        };
        request.Headers.Add("DPoP", manager.DPoPProof(now, "POST", Origin + TokenPath));
        using var tok = await anon.SendAsync(request, ct);
        tok.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = JsonDocument.Parse(await tok.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
        return new Setup(db, factory, anon, admin, manager, clientId, id, token);
    }

    private static HttpRequestMessage SelfRequest(
        Setup s, string? token = "", string? proof = "", string method = "GET", string path = SelfPath, string? htu = null,
        string? htm = null, TestAnalystManager? signer = null, string? ath = null, bool omitAth = false, string? jti = null)
    {
        var t = token == "" ? s.Token : token;
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (t is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + t);
        }

        var p = proof == "" ? (signer ?? s.Manager).DPoPProof(
            s.Factory.Time.GetUtcNow(), htm ?? method, htu ?? Origin + path, omitAth ? null : (ath ?? t), jti) : proof;
        if (p is not null)
        {
            request.Headers.Add("DPoP", p);
        }

        return request;
    }

    [Fact]
    public async Task ValidCallReturnsOwnRegistration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var r = await s.Anon.SendAsync(SelfRequest(s), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var root = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        root.GetProperty("managerId").GetString().ShouldBe(s.ClientId);
        root.GetProperty("state").GetString().ShouldBe("active");
        root.GetProperty("clubName").GetString().ShouldBe("Registry Club");
        root.GetProperty("keyThumbprint").GetString().ShouldBe(s.Manager.Thumbprint);
    }

    [Fact]
    public async Task HandlerYieldsPrincipalWithExactClaimsAndConstantsHoldValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        AnalystManagerClaimTypes.ManagerId.ShouldBe("client_id");
        AnalystManagerClaimTypes.RegistrationId.ShouldBe("socalytics:registration_id");
        AnalystManagerClaimTypes.Scope.ShouldBe("scope");
        AnalystManagerDPoPDefaults.ScopeValue.ShouldBe("analyst-manager");
        AnalystManagerDPoPDefaults.AuthenticationScheme.ShouldBe("AnalystManagerDPoP");

        await using var scope = s.Factory.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("localhost");
        context.Request.Method = "GET";
        context.Request.Path = SelfPath;
        context.Request.Headers.Authorization = "DPoP " + s.Token;
        context.Request.Headers["DPoP"] = s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "GET", Origin + SelfPath, s.Token);
        var result = await scope.ServiceProvider.GetRequiredService<IAuthenticationService>()
            .AuthenticateAsync(context, AnalystManagerDPoPDefaults.AuthenticationScheme);
        result.Succeeded.ShouldBeTrue();
        result.Principal!.FindFirst(AnalystManagerClaimTypes.ManagerId)!.Value.ShouldBe(s.ClientId);
        result.Principal.FindFirst(AnalystManagerClaimTypes.RegistrationId)!.Value.ShouldBe(s.Id.ToString());
        result.Principal.FindFirst(AnalystManagerClaimTypes.Scope)!.Value.ShouldBe("analyst-manager");
        result.Principal.Claims.Count().ShouldBe(3);
    }

    [Fact]
    public async Task InvalidCredentialsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var other = new TestAnalystManager();
        var attempts = new (string Name, Func<HttpRequestMessage> Build, string Error)[]
        {
            ("no-token", () => SelfRequest(s, token: null), "invalid_token"),
            ("unknown-token", () => SelfRequest(s, token: "unknown"), "invalid_token"),
            ("no-ath", () => SelfRequest(s, omitAth: true), "invalid_dpop_proof"),
            ("wrong-ath", () => SelfRequest(s, ath: "other"), "invalid_dpop_proof"),
            ("htu", () => SelfRequest(s, htu: Origin + "/x"), "invalid_dpop_proof"),
            ("htm", () => SelfRequest(s, htm: "POST"), "invalid_dpop_proof"),
            ("other-key", () => SelfRequest(s, signer: other), "invalid_dpop_proof"),
            ("no-proof", () => SelfRequest(s, proof: null), "invalid_dpop_proof"),
        };
        foreach (var (name, build, error) in attempts)
        {
            using var r = await s.Anon.SendAsync(build(), ct);
            r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, name);
            r.Headers.WwwAuthenticate.ToString().ShouldBe($"DPoP error=\"{error}\"", name);
        }

        using var first = await s.Anon.SendAsync(SelfRequest(s, jti: "fixed"), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var replay = await s.Anon.SendAsync(SelfRequest(s, jti: "fixed"), ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExpiredTokenAndNonHttpsAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var http = s.Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
        });
        using var plain = await http.SendAsync(SelfRequest(s), ct);
        plain.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        s.Factory.Time.Advance(TimeSpan.FromMinutes(6));
        using var expired = await s.Anon.SendAsync(SelfRequest(s), ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CookiesAndDPoPCrossNeitherBoundary()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        using var cookie = await s.Admin.SendAsync(HttpMethod.Get, SelfPath, null, ct);
        cookie.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var human = await s.Anon.SendAsync(SelfRequest(s, path: "/api/v1/analyst-manager-registrations"), ct);
        human.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ChangedStampIdInvalidatesTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartAsync(ct);
        await using (var c = new NpgsqlConnection(s.Db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand("UPDATE socalytics.analyst_manager_registration SET stamp_id = 'another-stamp'", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        using var r = await s.Anon.SendAsync(SelfRequest(s), ct);
        r.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}

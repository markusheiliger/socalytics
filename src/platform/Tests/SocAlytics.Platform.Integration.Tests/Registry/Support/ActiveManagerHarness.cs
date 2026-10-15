using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

/// <summary>A host with one Active Analyst Manager that calls the self endpoints through the real DPoP path.</summary>
internal sealed class ActiveManagerHarness : IAsyncDisposable
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string RegistrationsPath = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";

    private readonly TestAnalystManager _manager = new();
    private string _clientId = string.Empty;
    private string _token = string.Empty;

    private ActiveManagerHarness(IsolatedDatabase db, PlatformApiFactory factory, HttpClient anon, ApiSession admin)
    {
        Db = db;
        Factory = factory;
        Anon = anon;
        Admin = admin;
    }

    public IsolatedDatabase Db { get; }

    public PlatformApiFactory Factory { get; }

    public HttpClient Anon { get; }

    public ApiSession Admin { get; }

    public Guid RegistrationId { get; private set; }

    public static async Task<ActiveManagerHarness> StartAsync(
        PostgresContainerFixture postgres, IReadOnlyDictionary<string, string?> settings, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = new PlatformApiFactory(
            db,
            new Dictionary<string, string?>
            {
                ["ClubDisplayName"] = "Registry Club",
                ["FirstClubAdmin:AccountName"] = "registry-admin",
                ["FirstClubAdmin:InitialPassword"] = "Initial-Admin-Pass-1234",
            },
            configureServices: services => services.Configure<SocAlytics.Platform.Application.Registry.RegistryOptions>(o =>
            {
                o.AllowSoftwareKeys = true;
                o.RateLimits.PairingCreationPerMinute = 10_000;
                o.RateLimits.ActivationPerMinute = 10_000;
                o.RateLimits.CodeSubmissionPerMinute = 10_000;
                o.RateLimits.TokenPerMinute = 10_000;
            }),
            extraSettings: settings);
        factory.Time.Set(DateTimeOffset.UtcNow);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        var harness = new ActiveManagerHarness(db, factory, factory.CreateApiClient(), admin);
        await harness.ActivateAsync(ct);
        return harness;
    }

    /// <summary>Pairs a second Manager and leaves it pending approval; returns its registration id.</summary>
    public async Task<Guid> PairPendingAsync(CancellationToken ct)
    {
        var pending = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(Anon, RegistryTestHost.PairingBody(pending), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        using var submitted = await Admin.SendAsync(HttpMethod.Post, RegistrationsPath,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        return JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
    }

    public async Task RevokeAsync(CancellationToken ct)
    {
        using var revoke = await Admin.SendAsync(HttpMethod.Post, $"{RegistrationsPath}/{RegistrationId}/revocation", new { reason = "lost" }, ct);
        revoke.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = JsonContent.Create(new { });
        }

        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + _token);
        request.Headers.Add("DPoP", _manager.DPoPProof(Factory.Time.GetUtcNow(), method.Method, Origin + path, _token));
        return Anon.SendAsync(request, ct);
    }

    public async Task<string> QueryTextAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(Db.AppConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    public async ValueTask DisposeAsync()
    {
        Admin.Dispose();
        Anon.Dispose();
        _manager.Dispose();
        await Factory.DisposeAsync();
        await Db.DisposeAsync();
    }

    private async Task ActivateAsync(CancellationToken ct)
    {
        using var pair = await RegistryTestHost.PairAsync(Anon, RegistryTestHost.PairingBody(_manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var submitted = await Admin.SendAsync(HttpMethod.Post, RegistrationsPath,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        RegistrationId = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var approved = await Admin.SendAsync(HttpMethod.Post, $"{RegistrationsPath}/{RegistrationId}/approval", null, ct);
        approved.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        using var status = await RegistryTestHost.StatusAsync(Anon, handle, ct);
        var challenge = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = _manager.ActivationProof(Factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
        using var activated = await Anon.PostAsJsonAsync("/api/v1/analyst-managers/pairings/activation", new { pollingHandle = handle, proof }, ct);
        activated.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        _clientId = JsonDocument.Parse(await activated.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("managerId").GetString()!;

        var now = Factory.Time.GetUtcNow();
        var request = new HttpRequestMessage(HttpMethod.Post, TokenPath)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = _manager.ClientAssertion(now, Origin + TokenPath, _clientId),
                ["client_id"] = _clientId,
            }),
        };
        request.Headers.Add("DPoP", _manager.DPoPProof(now, "POST", Origin + TokenPath));
        using var token = await Anon.SendAsync(request, ct);
        token.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        _token = JsonDocument.Parse(await token.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.NKeys;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

[Collection("NatsFaultInjection")]
public sealed class QueueRevocationTests(PostgresContainerFixture postgres, NatsJwtContainerFixture nats)
    : IClassFixture<NatsJwtContainerFixture>
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string IssuePath = "/api/v1/analyst-managers/self/queue-credentials";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";
    private const string Capability = "dev-segment-capability";

    private sealed record Manager(TestAnalystManager Device, Guid RegistrationId, string ClientId, string Token);

    private sealed record Issued(KeyPair Pair, string UserJwt, string InboxPrefix);

    private PlatformApiFactory CreateFactory(IsolatedDatabase db, bool workers)
    {
        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
            ["Analysis:Workers:Enabled"] = workers ? "true" : "false",
        };
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
                o.RateLimits.StatusPerMinute = 10_000;
                o.RateLimits.ActivationPerMinute = 10_000;
                o.RateLimits.CodeSubmissionPerMinute = 10_000;
            }),
            extraSettings: settings);
        factory.Time.Set(DateTimeOffset.UtcNow);
        return factory;
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin)> StartAsync(
        bool workers, CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = CreateFactory(db, workers);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        return (db, factory, factory.CreateApiClient(), admin);
    }

    private static async Task<Manager> ActivateAsync(PlatformApiFactory factory, HttpClient anon, ApiSession admin, CancellationToken ct)
    {
        var device = new TestAnalystManager();
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(device), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        using var ok = await admin.SendAsync(HttpMethod.Post, $"{Path}/{id}/approval", null, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var st = await RegistryTestHost.StatusAsync(anon, handle, ct);
        var challenge = JsonDocument.Parse(await st.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("challenge").GetString()!;
        var proof = device.ActivationProof(factory.Time.GetUtcNow(), Origin + "/api/v1/analyst-managers/pairings/activation", challenge);
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
                ["client_assertion"] = device.ClientAssertion(now, Origin + TokenPath, clientId),
                ["client_id"] = clientId,
            }),
        };
        request.Headers.Add("DPoP", device.DPoPProof(now, "POST", Origin + TokenPath));
        using var r = await anon.SendAsync(request, ct);
        r.StatusCode.ShouldBe(HttpStatusCode.OK, await r.Content.ReadAsStringAsync(ct));
        var token = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("access_token").GetString()!;
        return new Manager(device, id, clientId, token);
    }

    private static async Task<Issued> IssueAsync(PlatformApiFactory factory, HttpClient anon, Manager manager, CancellationToken ct)
    {
        var pair = KeyPair.CreatePair(PrefixByte.User);
        var request = new HttpRequestMessage(HttpMethod.Post, IssuePath)
        {
            Content = JsonContent.Create(new
            {
                userPublicKey = pair.GetPublicKey(),
                capabilities = new[] { new { id = Capability, version = "1" } },
            }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + manager.Token);
        request.Headers.Add("DPoP", manager.Device.DPoPProof(factory.Time.GetUtcNow(), "POST", Origin + IssuePath, manager.Token));
        using var response = await anon.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        return new Issued(pair, body.GetProperty("userJwt").GetString()!, body.GetProperty("inboxPrefix").GetString()!);
    }

    private NatsConnection Connection(Issued issued) => new(NatsOpts.Default with
    {
        Url = nats.ConnectionString,
        AuthOpts = new NatsAuthOpts { Jwt = issued.UserJwt, Seed = issued.Pair.GetSeed() },
        InboxPrefix = issued.InboxPrefix,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        MaxReconnectRetry = 0,
    });

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.MigratorConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    private static async Task<bool> RefusedAsync(NatsConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.ConnectAsync();
            await connection.PingAsync(ct);
            return false;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return true;
        }
    }

    [Fact]
    public async Task Revocation_disconnects_the_manager_within_five_seconds_refuses_reconnect_and_spares_other_managers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, anon, admin) = await StartAsync(false, ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var anonScope = anon;
        using var adminScope = admin;
        var revoked = await ActivateAsync(factory, anon, admin, ct);
        var other = await ActivateAsync(factory, anon, admin, ct);
        var revokedIssued = await IssueAsync(factory, anon, revoked, ct);
        var otherIssued = await IssueAsync(factory, anon, other, ct);

        await using var revokedConnection = Connection(revokedIssued);
        await using var otherConnection = Connection(otherIssued);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        revokedConnection.ConnectionDisconnected += (_, _) =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };
        await revokedConnection.ConnectAsync();
        await revokedConnection.PingAsync(ct);
        await otherConnection.ConnectAsync();
        await otherConnection.PingAsync(ct);

        using var revoke = await admin.SendAsync(HttpMethod.Post, $"{Path}/{revoked.RegistrationId}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(ct));

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        (await RefusedAsync(Connection(revokedIssued), ct)).ShouldBeTrue();
        (await CountAsync(db,
            "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NOT NULL AND revocation_pushed_at IS NOT NULL", ct)).ShouldBe(1);
        (await CountAsync(db,
            "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revoked_at IS NULL", ct)).ShouldBe(1);

        await otherConnection.PingAsync(ct);
        await using var otherReconnect = Connection(otherIssued);
        await otherReconnect.ConnectAsync();
        await otherReconnect.PingAsync(ct);
    }

    [Fact]
    public async Task Worker_retries_a_pending_push_after_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, anon, admin) = await StartAsync(false, ct);
        await using var dbScope = db;
        using var anonScope = anon;
        using var adminScope = admin;
        var manager = await ActivateAsync(factory, anon, admin, ct);
        var issued = await IssueAsync(factory, anon, manager, ct);

        // A revocation whose push failed leaves the credential revoked and not pushed.
        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE socalytics.analyst_manager_queue_credential SET revoked_at = now() WHERE revoked_at IS NULL", c);
            (await cmd.ExecuteNonQueryAsync(ct)).ShouldBe(1);
        }

        await using (var connection = Connection(issued))
        {
            (await RefusedAsync(connection, ct)).ShouldBeFalse();
        }

        await factory.DisposeAsync();
        await using var restarted = CreateFactory(db, true);
        await restarted.WaitUntilHealthyAsync(ct);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await CountAsync(db,
            "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revocation_pushed_at IS NOT NULL", ct) == 0)
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "The worker did not push the pending revocation.");
            await Task.Delay(250, ct);
        }

        (await RefusedAsync(Connection(issued), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_push_throws_and_stays_pending_until_the_next_push_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, anon, admin) = await StartAsync(false, ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var anonScope = anon;
        using var adminScope = admin;
        var manager = await ActivateAsync(factory, anon, admin, ct);
        var issued = await IssueAsync(factory, anon, manager, ct);
        await using (var c = new NpgsqlConnection(db.MigratorConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE socalytics.analyst_manager_queue_credential SET revoked_at = now() WHERE revoked_at IS NULL", c);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var revoker = factory.Services.GetRequiredService<IQueueAccessRevoker>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => revoker.PushPendingAsync(cancelled.Token));
        (await CountAsync(db,
            "SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE revocation_pushed_at IS NULL", ct)).ShouldBe(1);

        (await revoker.PushPendingAsync(ct)).ShouldBe(1);
        (await RefusedAsync(Connection(issued), ct)).ShouldBeTrue();
    }
}

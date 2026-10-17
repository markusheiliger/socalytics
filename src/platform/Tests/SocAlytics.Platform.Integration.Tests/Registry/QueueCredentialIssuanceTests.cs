using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.NKeys;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.QueueCredentials;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;
using System.Net.Http.Json;

namespace SocAlytics.Platform.Integration.Tests.Registry;

[Collection("NatsFaultInjection")]
public sealed class QueueCredentialIssuanceTests(PostgresContainerFixture postgres, NatsJwtContainerFixture nats)
    : IClassFixture<NatsJwtContainerFixture>
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string IssuePath = "/api/v1/analyst-managers/self/queue-credentials";
    private const string Path = "/api/v1/analyst-manager-registrations";
    private const string Origin = "https://localhost";
    private const string Stream = "SOCALYTICS_ANALYSIS_JOBS";
    private const string Capability = "dev-segment-capability";

    private sealed record Setup(
        IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin,
        TestAnalystManager Manager, string ClientId, Guid RegistrationId, string Token) : IAsyncDisposable
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

    private PlatformApiFactory CreateFactory(IsolatedDatabase db)
    {
        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = "true",
            ["Analysis:Workers:Enabled"] = "false",
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
                o.SoftwareKeys = SoftwareKeyPolicy.All;
                o.RateLimits.PairingCreationPerMinute = 10_000;
                o.RateLimits.StatusPerMinute = 10_000;
                o.RateLimits.ActivationPerMinute = 10_000;
                o.RateLimits.CodeSubmissionPerMinute = 10_000;
            }),
            extraSettings: settings);
        factory.Time.Set(DateTimeOffset.UtcNow);
        return factory;
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin)> StartHostAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = CreateFactory(db);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        return (db, factory, factory.CreateApiClient(), admin);
    }

    private static async Task<(Guid Id, string Handle, string Code, string Fingerprint)> PairAsync(
        PlatformApiFactory factory, HttpClient anon, ApiSession admin, TestAnalystManager manager, CancellationToken ct)
    {
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        return (id, handle, "", "");
    }

    private async Task<Setup> StartActiveAsync(CancellationToken ct)
    {
        var (db, factory, anon, admin) = await StartHostAsync(ct);
        var manager = new TestAnalystManager();
        var (id, handle, _, _) = await PairAsync(factory, anon, admin, manager, ct);
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

    private static Task<HttpResponseMessage> IssueAsync(Setup s, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, IssuePath) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "POST", Origin + IssuePath, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static object Request(string userPublicKey, params (string Id, string Version)[] capabilities) => new
    {
        userPublicKey,
        capabilities = capabilities.Select(c => new { id = c.Id, version = c.Version }).ToArray(),
    };

    private static string NewUserKey(out KeyPair pair)
    {
        pair = KeyPair.CreatePair(PrefixByte.User);
        return pair.GetPublicKey();
    }

    private static async Task<long> CountAsync(Setup s, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(s.Db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<OperationResult<IssuedQueueCredentials>> HandleAsync(
        PlatformApiFactory factory, Guid registrationId, string userPublicKey, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IssueQueueCredentialsHandler>().HandleAsync(
            new IssueQueueCredentialsCommand(registrationId, userPublicKey, [new CapabilityReference(Capability, "1.0.0")]), ct);
    }

    private async Task<NatsConnection> ConnectAsync(JsonElement issued, KeyPair seedPair, CancellationToken ct)
    {
        var connection = new NatsConnection(NatsOpts.Default with
        {
            Url = nats.ConnectionString,
            AuthOpts = new NatsAuthOpts { Jwt = issued.GetProperty("userJwt").GetString(), Seed = seedPair.GetSeed() },
            InboxPrefix = issued.GetProperty("inboxPrefix").GetString()!,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        });
        await connection.ConnectAsync();
        await connection.PingAsync(ct);
        return connection;
    }

    [Fact]
    public async Task Issues_credentials_for_an_active_manager_and_records_the_issuance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        var key = NewUserKey(out _);

        using var response = await IssueAsync(s, Request(key, (Capability, "1.0.0"), ("unknown-capability", "1.0.0")), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        body.GetProperty("stream").GetString().ShouldBe(Stream);
        body.GetProperty("maxConcurrentAnalysts").GetInt32().ShouldBe(2);
        body.GetProperty("queueUrl").GetString().ShouldNotBeNullOrEmpty();
        body.GetProperty("inboxPrefix").GetString().ShouldStartWith("_INBOX_");
        body.GetProperty("userJwt").GetString().ShouldNotBeNullOrEmpty();
        var consumers = body.GetProperty("consumers").EnumerateArray().ToList();
        consumers.Count.ShouldBe(1);
        consumers[0].GetProperty("capability").GetString().ShouldBe(Capability);
        consumers[0].GetProperty("consumer").GetString().ShouldBe("analyst-" + Capability);
        (await CountAsync(s, $"SELECT count(*) FROM socalytics.analyst_manager_queue_credential WHERE user_public_key = '{key}'", ct)).ShouldBe(1);

        var jwt = body.GetProperty("userJwt").GetString()!;
        s.Factory.Logs.Entries.ShouldAllBe(e => !e.Message.Contains(jwt) && (e.Exception == null || !e.Exception.Contains(jwt)));
    }

    [Fact]
    public async Task Refuses_a_reused_or_malformed_user_key_and_malformed_capabilities()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        var key = NewUserKey(out _);
        using (var first = await IssueAsync(s, Request(key, (Capability, "1.0.0")), ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        using var reused = await IssueAsync(s, Request(key, (Capability, "1.0.0")), ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonDocument.Parse(await reused.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString().ShouldBe("user-key-reused");

        foreach (var bad in new[] { "", "not-a-key", KeyPair.CreatePair(PrefixByte.Account).GetPublicKey() })
        {
            using var invalid = await IssueAsync(s, Request(bad, (Capability, "1.0.0")), ct);
            invalid.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString().ShouldBe("invalid-user-key");
        }

        using var missing = await IssueAsync(s, new { userPublicKey = NewUserKey(out _) }, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonDocument.Parse(await missing.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString().ShouldBe("invalid-capabilities");
    }

    [Fact]
    public async Task Refuses_pending_unregistered_and_revoked_managers()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, anon, admin) = await StartHostAsync(ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var anonScope = anon;
        using var adminScope = admin;
        using var pendingManager = new TestAnalystManager();
        var (pendingId, _, _, _) = await PairAsync(factory, anon, admin, pendingManager, ct);

        var pending = await HandleAsync(factory, pendingId, NewUserKey(out _), ct);
        pending.IsSuccess.ShouldBeFalse();
        pending.Failure.Code.ShouldBe("manager-not-active");
        pending.Failure.Kind.ShouldBe(OperationFailureKind.Forbidden);

        var unregistered = await HandleAsync(factory, Guid.CreateVersion7(), NewUserKey(out _), ct);
        unregistered.IsSuccess.ShouldBeFalse();
        unregistered.Failure.Kind.ShouldBe(OperationFailureKind.Unauthenticated);

        await using var s = await StartActiveAsync(ct);
        using var revoke = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.RegistrationId}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await revoke.Content.ReadAsStringAsync(ct));

        var revoked = await HandleAsync(s.Factory, s.RegistrationId, NewUserKey(out _), ct);
        revoked.IsSuccess.ShouldBeFalse();
        revoked.Failure.Code.ShouldBe("manager-not-active");

        using var overHttp = await IssueAsync(s, Request(NewUserKey(out _), (Capability, "1.0.0")), ct);
        overHttp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await CountAsync(s, "SELECT count(*) FROM socalytics.analyst_manager_queue_credential", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Issued_jwt_pulls_and_acknowledges_but_cannot_publish_administer_or_subscribe_outside_its_inbox()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        var platform = s.Factory.Services.GetRequiredService<NatsConnectionProvider>();
        platform.EnsureStarted();
        await s.Factory.Services.GetRequiredService<NatsTopology>().EnsureAsync(ct);

        var key = NewUserKey(out var pair);
        using var response = await IssueAsync(s, Request(key, (Capability, "1.0.0")), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        var issued = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        await using var manager = await ConnectAsync(issued, pair, ct);

        var messageId = Guid.NewGuid().ToString("N");
        var transport = s.Factory.Services.GetRequiredService<IMessageTransport>();
        (await transport.PublishAsync(
            new TransportMessage("analysis.jobs.ready." + Capability, messageId, Encoding.UTF8.GetBytes("{}")), ct))
            .ShouldBe(TransportPublishResult.Published);

        var js = new NatsJSContext(manager);
        var consumer = await js.GetConsumerAsync(Stream, "analyst-" + Capability, ct);
        var received = false;
        await foreach (var message in consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(5) },
            serializer: NatsRawSerializer<byte[]>.Default,
            cancellationToken: ct))
        {
            await message.AckAsync(cancellationToken: ct);
            received = true;
        }

        received.ShouldBeTrue();

        using (var denied = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            denied.CancelAfter(TimeSpan.FromSeconds(3));
            await Should.ThrowAsync<Exception>(async () => await js.PublishAsync(
                "analysis.jobs.ready." + Capability, "{}", cancellationToken: denied.Token));
        }

        using (var denied = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            denied.CancelAfter(TimeSpan.FromSeconds(3));
            await Should.ThrowAsync<Exception>(async () => await js.DeleteStreamAsync(Stream, denied.Token));
        }

        using (var denied = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            denied.CancelAfter(TimeSpan.FromSeconds(3));
            await Should.ThrowAsync<Exception>(async () => await js.CreateOrUpdateConsumerAsync(
                Stream, new ConsumerConfig("rogue") { DurableName = "rogue" }, denied.Token));
        }

        var probeReceived = false;
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listen.CancelAfter(TimeSpan.FromSeconds(3));
        var listener = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in manager.SubscribeAsync<byte[]>("analysis.probe", serializer: NatsRawSerializer<byte[]>.Default, cancellationToken: listen.Token))
                {
                    probeReceived = true;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, ct);
        await Task.Delay(500, ct);
        await platform.Connection.PublishAsync("analysis.probe", "x", cancellationToken: ct);
        await listener;
        probeReceived.ShouldBeFalse();
    }
}

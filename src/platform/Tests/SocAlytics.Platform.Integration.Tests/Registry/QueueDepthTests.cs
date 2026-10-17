using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Json.Schema;
using Microsoft.Extensions.DependencyInjection;
using NATS.Client.Core;
using NATS.Client.JetStream;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Registry.QueueDepth;
using SocAlytics.Platform.Infrastructure.Messaging.Nats;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

[Collection("NatsFaultInjection")]
public sealed class QueueDepthTests(PostgresContainerFixture postgres, NatsJwtContainerFixture nats)
    : IClassFixture<NatsJwtContainerFixture>
{
    private const string TokenPath = "/api/v1/analyst-managers/token";
    private const string DepthPath = "/api/v1/analyst-managers/self/queue-depth";
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

    private PlatformApiFactory CreateFactory(IsolatedDatabase db, string? natsUrl, bool provision)
    {
        var settings = new Dictionary<string, string?>(nats.AuthenticationSettings)
        {
            ["ConnectionStrings:nats"] = natsUrl ?? nats.ConnectionString,
            ["Messaging:Nats:ProvisionTopology"] = provision ? "true" : "false",
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
                o.SoftwareKeys = SocAlytics.Platform.Application.Registry.SoftwareKeyPolicy.All;
                o.RateLimits.PairingCreationPerMinute = 10_000;
                o.RateLimits.StatusPerMinute = 10_000;
                o.RateLimits.ActivationPerMinute = 10_000;
                o.RateLimits.CodeSubmissionPerMinute = 10_000;
            }),
            extraSettings: settings);
        factory.Time.Set(DateTimeOffset.UtcNow);
        return factory;
    }

    private async Task<(IsolatedDatabase Db, PlatformApiFactory Factory, HttpClient Anon, ApiSession Admin)> StartHostAsync(
        CancellationToken ct, string? natsUrl = null, bool provision = true)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var factory = CreateFactory(db, natsUrl, provision);
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "admin-1", clubRoles: ["club-admin", "registrar"], cancellationToken: ct);
        var admin = await ApiSession.SignInAsync(factory, "admin-1", TestMembers.DefaultPassword, ct);
        return (db, factory, factory.CreateApiClient(), admin);
    }

    private static async Task<(Guid Id, string Handle)> PairAsync(
        HttpClient anon, ApiSession admin, TestAnalystManager manager, CancellationToken ct)
    {
        using var pair = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        var root = JsonDocument.Parse(await pair.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        using var sub = await admin.SendAsync(HttpMethod.Post, Path,
            new { pairingCode = root.GetProperty("pairingCode").GetString(), deviceFingerprint = root.GetProperty("deviceFingerprint").GetString() }, ct);
        var id = JsonDocument.Parse(await sub.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("registrationId").GetGuid();
        return (id, handle);
    }

    private async Task<Setup> StartActiveAsync(CancellationToken ct, string? natsUrl = null, bool provision = true)
    {
        var (db, factory, anon, admin) = await StartHostAsync(ct, natsUrl, provision);
        var manager = new TestAnalystManager();
        var (id, handle) = await PairAsync(anon, admin, manager, ct);
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

    private static Task<HttpResponseMessage> DepthAsync(Setup s, string query, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, DepthPath + query);
        request.Headers.TryAddWithoutValidation("Authorization", "DPoP " + s.Token);
        request.Headers.Add("DPoP", s.Manager.DPoPProof(s.Factory.Time.GetUtcNow(), "GET", Origin + DepthPath, s.Token));
        return s.Anon.SendAsync(request, ct);
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response, CancellationToken ct) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();

    private static async Task PublishAsync(Setup s, int count, CancellationToken ct)
    {
        var transport = s.Factory.Services.GetRequiredService<IMessageTransport>();
        for (var i = 0; i < count; i++)
        {
            (await transport.PublishAsync(
                new TransportMessage("analysis.jobs.ready." + Capability, Guid.NewGuid().ToString("N"), Encoding.UTF8.GetBytes("{}")), ct))
                .ShouldBe(TransportPublishResult.Published);
        }
    }

    private static async Task ProvisionAsync(Setup s, CancellationToken ct)
    {
        s.Factory.Services.GetRequiredService<NatsConnectionProvider>().EnsureStarted();
        await s.Factory.Services.GetRequiredService<NatsTopology>().EnsureAsync(ct);
        var js = new NatsJSContext(s.Factory.Services.GetRequiredService<NatsConnectionProvider>().Connection);
        await js.PurgeStreamAsync(Stream, new NATS.Client.JetStream.Models.StreamPurgeRequest(), ct);
    }

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(File.ReadAllText(System.IO.Path.Combine(
        AppContext.BaseDirectory, "contracts", "analyst-manager", "queue-depth", "v1", "queue-depth.schema.json"))));

    private static void ShouldMatchSchema(JsonElement body)
    {
        Schema.Value.Evaluate(body, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid.ShouldBeTrue(body.GetRawText());
    }

    [Fact]
    public async Task Counts_ready_jobs_per_capability_and_omits_unknown_ids()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        await ProvisionAsync(s, ct);

        using (var empty = await DepthAsync(s, $"?capability={Capability}&capability=unknown-capability&capability={Capability}", ct))
        {
            empty.StatusCode.ShouldBe(HttpStatusCode.OK, await empty.Content.ReadAsStringAsync(ct));
            empty.Headers.CacheControl!.NoStore.ShouldBeTrue();
            var body = await BodyAsync(empty, ct);
            ShouldMatchSchema(body);
            body.GetProperty("total").GetInt64().ShouldBe(0);
            var items = body.GetProperty("capabilities").EnumerateArray().ToList();
            items.Count.ShouldBe(1);
            items[0].GetProperty("id").GetString().ShouldBe(Capability);
            items[0].GetProperty("waiting").GetInt64().ShouldBe(0);
        }

        await PublishAsync(s, 3, ct);
        using var response = await DepthAsync(s, $"?capability={Capability}", ct);
        var counted = await BodyAsync(response, ct);
        ShouldMatchSchema(counted);
        counted.GetProperty("capabilities")[0].GetProperty("waiting").GetInt64().ShouldBe(3);
        counted.GetProperty("total").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task Delivered_but_unacknowledged_jobs_are_not_counted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        await ProvisionAsync(s, ct);
        await PublishAsync(s, 3, ct);

        var js = new NatsJSContext(s.Factory.Services.GetRequiredService<NatsConnectionProvider>().Connection);
        var consumer = await js.GetConsumerAsync(Stream, "analyst-" + Capability, ct);
        await foreach (var _ in consumer.FetchAsync(
            new NatsJSFetchOpts { MaxMsgs = 1, Expires = TimeSpan.FromSeconds(5) },
            serializer: NatsRawSerializer<byte[]>.Default, cancellationToken: ct))
        {
        }

        using var response = await DepthAsync(s, $"?capability={Capability}", ct);
        (await BodyAsync(response, ct)).GetProperty("total").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Omits_a_known_capability_whose_consumer_does_not_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);
        await ProvisionAsync(s, ct);
        var js = new NatsJSContext(s.Factory.Services.GetRequiredService<NatsConnectionProvider>().Connection);
        await js.DeleteConsumerAsync(Stream, "analyst-" + Capability, ct);

        using var response = await DepthAsync(s, $"?capability={Capability}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        var body = await BodyAsync(response, ct);
        ShouldMatchSchema(body);
        body.GetProperty("capabilities").GetArrayLength().ShouldBe(0);
        body.GetProperty("total").GetInt64().ShouldBe(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?capability=")]
    [InlineData("?capability=Bad_Id")]
    [InlineData("?capability=-leading-dash")]
    public async Task Rejects_missing_or_invalid_capability_values(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);

        using var response = await DepthAsync(s, query, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response, ct)).GetProperty("errors")[0].GetProperty("field").GetString().ShouldBe("capability");
    }

    [Fact]
    public async Task Rejects_more_than_64_capability_values()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct);

        using var response = await DepthAsync(s, "?" + string.Join('&', Enumerable.Range(0, 65).Select(i => "capability=c" + i)), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Refuses_pending_unknown_and_revoked_registrations()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, factory, anon, admin) = await StartHostAsync(ct);
        await using var dbScope = db;
        await using var factoryScope = factory;
        using var anonScope = anon;
        using var adminScope = admin;
        using var pendingManager = new TestAnalystManager();
        var (pendingId, _) = await PairAsync(anon, admin, pendingManager, ct);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<ReadQueueDepthHandler>();
            var pending = await handler.HandleAsync(new ReadQueueDepthCommand(pendingId, [Capability]), ct);
            pending.IsSuccess.ShouldBeFalse();
            pending.Failure.Kind.ShouldBe(OperationFailureKind.Forbidden);
            pending.Failure.Code.ShouldBe("manager-not-active");

            var unknown = await handler.HandleAsync(new ReadQueueDepthCommand(Guid.CreateVersion7(), [Capability]), ct);
            unknown.Failure.Kind.ShouldBe(OperationFailureKind.Unauthenticated);
        }

        await using var s = await StartActiveAsync(ct);
        using var revoke = await s.Admin.SendAsync(HttpMethod.Post, $"{Path}/{s.RegistrationId}/revocation", new { reason = "device lost" }, ct);
        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await DepthAsync(s, $"?capability={Capability}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Answers_503_queue_unavailable_when_the_broker_cannot_be_reached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await StartActiveAsync(ct, natsUrl: "nats://127.0.0.1:1", provision: false);

        using var response = await DepthAsync(s, $"?capability={Capability}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await BodyAsync(response, ct)).GetProperty("type").GetString().ShouldBe("urn:socalytics:problem:queue-unavailable");
    }
}

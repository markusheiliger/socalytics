#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Diagnostics;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Queue;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Queue;

public class QueueCredentialProviderTests : IDisposable
{
    private const string Jwt = "jwt-secret-value";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-12T14:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly FakeLogCollector _logs = new();
    private readonly QueueCredentialProvider _provider;
    private readonly PlatformSession? _session;
    private readonly IReadOnlyList<QueueCapability> _capabilities = [new("player-detection", "1.0.0")];
    private PlatformSession? _current;

    public QueueCredentialProviderTests()
    {
        _platform = new FakePlatform(_time);
        var client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        _session = new PlatformSession(new Uri("https://platform.test"), key, "opaque-token");
        _current = _session;
        var factory = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        _provider = new QueueCredentialProvider(client, _time, factory.CreateLogger<QueueCredentialProvider>());
        _platform.Script(WorkOperation.QueueCredentials, () => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(Credentials(_time.GetUtcNow() + Lifetime), System.Text.Encoding.UTF8, "application/json"),
        });
    }

    public void Dispose() => _provider.Dispose();

    private static string Credentials(DateTimeOffset expiresAt) =>
        $$"""{"userJwt":"{{Jwt}}","expiresAt":"{{expiresAt:O}}","queueUrl":"tls://queue.test:4222","stream":"SOCALYTICS_ANALYSIS_JOBS","consumers":[{"capability":"player-detection","consumer":"analyst-player-detection"}],"inboxPrefix":"_INBOX_9f2c1e0a","maxConcurrentAnalysts":2}""";

    private int Requests => _platform.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/queue-credentials", StringComparison.Ordinal));

    private Task<TimeSpan> StepAsync() => _provider.StepAsync(() => _current, () => _capabilities, CancellationToken.None);

    [Fact]
    public async Task Requests_a_credential_with_an_ephemeral_public_key_and_renews_at_two_thirds()
    {
        var wait = await StepAsync();

        wait.ShouldBe(TimeSpan.FromMinutes(20));
        Requests.ShouldBe(1);
        _provider.Current!.ExpiresAt.ShouldBe(_time.GetUtcNow() + Lifetime);
        var body = JsonNode.Parse(_platform.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/queue-credentials", StringComparison.Ordinal)).Body)!;
        ((string)body["userPublicKey"]!).ShouldStartWith("U");
        body.ToJsonString().ShouldNotContain("SU");

        _time.Advance(TimeSpan.FromMinutes(19));
        (await StepAsync()).ShouldBe(TimeSpan.FromMinutes(1));
        Requests.ShouldBe(1);

        var first = _provider.Current;
        _time.Advance(TimeSpan.FromMinutes(1));
        (await StepAsync()).ShouldBe(TimeSpan.FromMinutes(20));
        Requests.ShouldBe(2);
        _provider.Current.ShouldNotBeSameAs(first);
    }

    [Fact]
    public async Task Every_credential_uses_a_fresh_user_key()
    {
        await StepAsync();
        _time.Advance(TimeSpan.FromMinutes(21));
        await StepAsync();

        var keys = _platform.Requests
            .Where(r => r.Uri.AbsolutePath.EndsWith("/queue-credentials", StringComparison.Ordinal))
            .Select(r => (string)JsonNode.Parse(r.Body)!["userPublicKey"]!)
            .ToList();
        keys.Count.ShouldBe(2);
        keys.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    public async Task No_request_while_inactive()
    {
        _current = null;

        (await StepAsync()).ShouldBe(QueueCredentialProvider.IdlePoll);

        Requests.ShouldBe(0);
        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Credential_is_discarded_when_the_manager_becomes_inactive()
    {
        await StepAsync();
        var changes = new List<QueueCredential?>();
        _provider.Changed += changes.Add;

        _current = null;
        await StepAsync();

        _provider.Current.ShouldBeNull();
        changes.ShouldBe([null]);
        Requests.ShouldBe(1);
    }

    [Fact]
    public async Task Credential_is_discarded_on_revocation_and_exit()
    {
        await StepAsync();
        _provider.Discard("revocation");
        _provider.Current.ShouldBeNull();

        await StepAsync();
        _provider.Current.ShouldNotBeNull();
        _provider.Dispose();
        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Refusal_discards_the_credential_and_transient_failures_retry()
    {
        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.ServiceUnavailable);
        (await StepAsync()).ShouldBe(QueueCredentialProvider.RetryDelay);
        _provider.Current.ShouldBeNull();

        _platform.Script(WorkOperation.QueueCredentials, HttpStatusCode.Forbidden,
            """{"type":"urn:socalytics:problem:manager-not-active","title":"t","status":403,"code":"manager-not-active","correlationId":"c"}""");
        (await StepAsync()).ShouldBe(QueueCredentialProvider.IdlePoll);
        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Seed_and_jwt_are_never_logged()
    {
        await StepAsync();
        var seed = _provider.Current!.Seed;
        _provider.Discard("revocation");

        var text = string.Join('\n', _logs.GetSnapshot().Select(r => r.Message));
        text.ShouldNotBeEmpty();
        text.ShouldNotContain(Jwt);
        text.ShouldNotContain(seed);
    }

    [Theory]
    [InlineData("nats://queue.test:4222", "Development", true)]
    [InlineData("nats://queue.test:4222", "Test", true)]
    [InlineData("nats://queue.test:4222", "Production", false)]
    [InlineData("nats://queue.test:4222", "Staging", false)]
    [InlineData("tls://queue.test:4222", "Production", true)]
    [InlineData("ws://queue.test:4222", "Development", false)]
    public void Plaintext_is_accepted_only_in_development_and_test(string url, string environment, bool allowed)
    {
        QueueConnection.IsAllowedScheme(url, new Env(environment)).ShouldBe(allowed);
    }

    [Fact]
    public async Task Connecting_over_plaintext_in_production_is_refused_before_any_network_use()
    {
        await StepAsync();
        var plaintext = new QueueCredentialProvider(
            new PlatformClient(new HttpClient(_platform), new ProofFactory(_time)), _time);
        _platform.Script(WorkOperation.QueueCredentials, () => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(
                Credentials(_time.GetUtcNow() + Lifetime).Replace("tls://", "nats://"), System.Text.Encoding.UTF8, "application/json"),
        });
        await plaintext.StepAsync(() => _session, () => _capabilities, CancellationToken.None);

        var error = await Should.ThrowAsync<PlatformException>(
            async () => await QueueConnection.ConnectAsync(plaintext.Current!, new Env("Production"), CancellationToken.None));

        error.Code.ShouldBe(ManagerDiagnosticCodes.InsecureEndpoint);
        plaintext.Dispose();
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "test";

        public string ContentRootPath { get; set; } = "/";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

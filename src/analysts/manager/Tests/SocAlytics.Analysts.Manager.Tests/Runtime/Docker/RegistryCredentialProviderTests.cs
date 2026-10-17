#pragma warning disable xUnit1051 // The fake platform answers synchronously; there is nothing to cancel.
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Platform;
using SocAlytics.Analysts.Manager.Runtime.Docker;
using SocAlytics.Analysts.Manager.Tests.Support;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class RegistryCredentialProviderTests : IDisposable
{
    private const string Token = "registry-token-secret";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-15T09:00:00Z"));
    private readonly FakePlatform _platform;
    private readonly FakeLogCollector _logs = new();
    private readonly RegistryCredentialProvider _provider;
    private PlatformSession? _current;

    public RegistryCredentialProviderTests()
    {
        _platform = new FakePlatform(_time);
        var client = new PlatformClient(new HttpClient(_platform), new ProofFactory(_time));
        var key = new InMemoryDeviceKeyProvider().ImportFixtureKey();
        _current = new PlatformSession(new Uri("https://platform.test"), key, "opaque-token");
        var factory = LoggerFactory.Create(b => b.AddProvider(new FakeLoggerProvider(_logs)));
        _provider = new RegistryCredentialProvider(client, _time, factory.CreateLogger<RegistryCredentialProvider>());
        Script(() => Bearer(_time.GetUtcNow() + Lifetime));
    }

    public void Dispose() => _provider.Dispose();

    private static string Bearer(DateTimeOffset expiresAt) =>
        $$"""{"registry":"registry.test","repositories":["socalytics/test-analyst"],"kind":"bearer","token":"{{Token}}","expiresAt":"{{expiresAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}}"}""";

    private static string Anonymous(DateTimeOffset expiresAt) =>
        $$"""{"registry":"registry.test","repositories":["socalytics/test-analyst"],"kind":"anonymous","expiresAt":"{{expiresAt.UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}}"}""";

    private void Script(Func<string> body) =>
        _platform.Script(WorkOperation.RegistryCredentials, () => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent(body(), System.Text.Encoding.UTF8, "application/json"),
        });

    private int Requests => _platform.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/registry-credentials", StringComparison.Ordinal));

    private Task<TimeSpan> StepAsync() => _provider.StepAsync(() => _current, CancellationToken.None);

    [Fact]
    public async Task Requests_an_empty_body_and_renews_at_eighty_percent_of_the_lifetime()
    {
        var wait = await StepAsync();

        wait.ShouldBe(TimeSpan.FromMinutes(4));
        Requests.ShouldBe(1);
        _provider.Current!.AuthToken.ShouldBe(Token);
        var sent = _platform.Requests.Single(r => r.Uri.AbsolutePath.EndsWith("/registry-credentials", StringComparison.Ordinal));
        sent.Method.ShouldBe(HttpMethod.Post);
        JsonNode.Parse(sent.Body)!.AsObject().Count.ShouldBe(0);

        _time.Advance(TimeSpan.FromMinutes(3));
        (await StepAsync()).ShouldBe(TimeSpan.FromMinutes(1));
        Requests.ShouldBe(1);

        _time.Advance(TimeSpan.FromMinutes(1));
        await StepAsync();
        Requests.ShouldBe(2);
        _provider.Current!.ExpiresAt.ShouldBe(_time.GetUtcNow() + Lifetime);
    }

    [Fact]
    public async Task An_anonymous_credential_yields_no_registry_auth_token()
    {
        Script(() => Anonymous(_time.GetUtcNow() + Lifetime));

        await StepAsync();

        _provider.Current!.Kind.ShouldBe("anonymous");
        _provider.Current.AuthToken.ShouldBeNull();
    }

    [Fact]
    public async Task Revocation_clears_the_credential()
    {
        await StepAsync();
        var changes = new List<RegistryPullCredential?>();
        _provider.Changed += changes.Add;

        _current = null;
        (await StepAsync()).ShouldBe(RegistryCredentialProvider.IdlePoll);

        _provider.Current.ShouldBeNull();
        changes.ShouldBe([null]);
        Requests.ShouldBe(1);
    }

    [Fact]
    public async Task A_refused_request_surfaces_as_a_missing_credential()
    {
        await StepAsync();
        _platform.Script(WorkOperation.RegistryCredentials, HttpStatusCode.Conflict,
            """{"type":"urn:socalytics:problem:workflow-not-configured","title":"t","status":409,"code":"workflow-not-configured","correlationId":"c"}""");
        _time.Advance(TimeSpan.FromMinutes(4));

        (await StepAsync()).ShouldBe(RegistryCredentialProvider.IdlePoll);

        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task An_unreachable_platform_keeps_a_valid_credential_and_retries()
    {
        await StepAsync();
        _platform.Script(WorkOperation.RegistryCredentials, () => throw new HttpRequestException("unreachable"));
        _time.Advance(TimeSpan.FromMinutes(4));

        (await StepAsync()).ShouldBe(RegistryCredentialProvider.RetryDelay);

        _provider.Current.ShouldNotBeNull();
        _time.Advance(TimeSpan.FromMinutes(1));
        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task A_bearer_credential_without_a_token_is_not_accepted()
    {
        Script(() => $$"""{"registry":"registry.test","repositories":[],"kind":"bearer","expiresAt":"{{(_time.GetUtcNow() + Lifetime).UtcDateTime:yyyy-MM-dd'T'HH:mm:ss'Z'}}"}""");

        await StepAsync();

        _provider.Current.ShouldBeNull();
    }

    [Fact]
    public async Task The_token_never_appears_in_logs_or_text_forms()
    {
        await StepAsync();
        _provider.Discard("test");

        _logs.GetSnapshot().ShouldNotBeEmpty();
        foreach (var record in _logs.GetSnapshot())
        {
            record.Message.ShouldNotContain(Token);
            (record.Exception?.ToString() ?? string.Empty).ShouldNotContain(Token);
        }

        new RegistryPullCredential("r", ["a"], "bearer", Token, _time.GetUtcNow(), _time.GetUtcNow()).ToString().ShouldNotContain(Token);
    }
}

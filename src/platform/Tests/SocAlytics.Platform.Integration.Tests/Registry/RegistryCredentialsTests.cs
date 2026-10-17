using System.Net;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistryCredentialsTests(PostgresContainerFixture postgres, OciRegistryContainerFixture registry)
    : IClassFixture<OciRegistryContainerFixture>
{
    private const string Path = "/api/v1/analyst-managers/self/registry-credentials";

    // The seeded development workflow stores its images under the repository "seed".
    private static Dictionary<string, string?> Settings(string host, string lifetime = "00:05:00") => new()
    {
        ["Registry:Images:RegistryHost"] = host,
        ["Registry:Images:RepositoryPrefix"] = "seed",
        ["Registry:Images:UseHttp"] = "true",
        ["Registry:Images:Username"] = "reader",
        ["Registry:Images:Token"] = "reader-secret-91c",
        ["Registry:Images:PullCredentialLifetime"] = lifetime,
    };

    [Fact]
    public async Task Issues_a_bearer_credential_limited_to_the_workflow_with_a_capped_lifetime_and_never_logs_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await TokenAuthRegistryFake.StartAsync();
        await using var s = await ActiveManagerHarness.StartAsync(postgres, Settings(fake.RegistryHost, "00:02:00"), ct);

        using var response = await s.SendAsync(HttpMethod.Post, Path, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        body.GetProperty("kind").GetString().ShouldBe("bearer");
        body.GetProperty("token").GetString().ShouldBe(TokenAuthRegistryFake.IssuedToken);
        body.GetProperty("registry").GetString().ShouldBe(fake.RegistryHost);
        body.GetProperty("repositories").EnumerateArray().Select(r => r.GetString()).ShouldBe(["seed"]);
        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        expiresAt.ShouldBeLessThanOrEqualTo(s.Factory.Time.GetUtcNow().AddMinutes(2).AddSeconds(1));
        expiresAt.ShouldBeGreaterThan(s.Factory.Time.GetUtcNow());

        fake.TokenQueries.ShouldHaveSingleItem().ShouldContain("scope=repository%3Aseed%3Apull");
        fake.TokenAuthorizations.ShouldHaveSingleItem()!.ShouldStartWith("Basic ");

        s.Factory.Logs.Entries.Any(e => (e.Message + e.Exception).Contains(TokenAuthRegistryFake.IssuedToken)).ShouldBeFalse();
        (await s.QueryTextAsync(
            "SELECT coalesce(string_agg(details::text || coalesce(reason_code, ''), ' '), '') FROM socalytics.security_audit_event", ct))
            .ShouldNotContain(TokenAuthRegistryFake.IssuedToken);
    }

    [Fact]
    public async Task Issues_an_anonymous_credential_without_a_token_when_the_registry_needs_no_authentication()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await ActiveManagerHarness.StartAsync(postgres, Settings(registry.RegistryHost), ct);

        using var response = await s.SendAsync(HttpMethod.Post, Path, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;
        body.GetProperty("kind").GetString().ShouldBe("anonymous");
        body.TryGetProperty("token", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Reports_registry_unavailable_when_the_token_service_refuses_the_platform()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fake = await TokenAuthRegistryFake.StartAsync();
        fake.RefuseTokens = true;
        await using var s = await ActiveManagerHarness.StartAsync(postgres, Settings(fake.RegistryHost), ct);

        using var response = await s.SendAsync(HttpMethod.Post, Path, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("code").GetString()
            .ShouldBe("registry-unavailable");
    }

    [Fact]
    public async Task Refuses_revoked_managers_and_unauthenticated_calls()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var s = await ActiveManagerHarness.StartAsync(postgres, Settings(registry.RegistryHost), ct);
        using var unauthenticated = await s.Anon.PostAsync(Path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), ct);
        unauthenticated.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await s.RevokeAsync(ct);
        using var revoked = await s.SendAsync(HttpMethod.Post, Path, ct);

        // Revocation removes the access tokens, so the Manager no longer authenticates at all.
        revoked.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }
}

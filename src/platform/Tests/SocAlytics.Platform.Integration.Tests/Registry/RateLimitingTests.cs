using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RateLimitingTests(PostgresContainerFixture postgres)
{
    private const string Path = "/api/v1/analyst-manager-registrations";

    [Fact]
    public async Task ExcessCallsGetIdenticalRateLimitedProblems()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        await using var factory = RegistryTestHost.Create(db, rateLimits: l =>
        {
            l.PairingCreationPerMinute = 2;
            l.StatusPerMinute = 2;
            l.CodeSubmissionPerMinute = 2;
        });
        await factory.WaitUntilHealthyAsync(ct);
        await TestMembers.SeedAsync(db, "reg-1", clubRoles: ["registrar"], cancellationToken: ct);
        await TestMembers.SeedAsync(db, "reg-2", clubRoles: ["registrar"], cancellationToken: ct);
        using var reg1 = await ApiSession.SignInAsync(factory, "reg-1", TestMembers.DefaultPassword, ct);
        using var reg2 = await ApiSession.SignInAsync(factory, "reg-2", TestMembers.DefaultPassword, ct);
        using var anon = factory.CreateApiClient();
        using var manager = new TestAnalystManager();

        using var first = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        var root = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct)).RootElement;
        var handle = root.GetProperty("pollingHandle").GetString()!;
        var code = root.GetProperty("pairingCode").GetString()!;

        // Status: existing versus unknown handle.
        (await RegistryTestHost.StatusAsync(anon, handle, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RegistryTestHost.StatusAsync(anon, "unknown-handle", ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        using var s1 = await RegistryTestHost.StatusAsync(anon, handle, ct);
        using var s2 = await RegistryTestHost.StatusAsync(anon, "unknown-handle", ct);
        s1.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        s2.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        s1.Headers.RetryAfter.ShouldNotBeNull();
        var b1 = RegistryTestHost.WithoutCorrelation(await s1.Content.ReadAsStringAsync(ct));
        b1.ShouldBe(RegistryTestHost.WithoutCorrelation(await s2.Content.ReadAsStringAsync(ct)));
        b1.ShouldContain("analyst-manager-rate-limited");

        // Pairing creation: the second call is allowed, the third is limited.
        using var manager2 = new TestAnalystManager();
        (await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager2), ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var p = await RegistryTestHost.PairAsync(anon, RegistryTestHost.PairingBody(manager), ct);
        p.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        RegistryTestHost.WithoutCorrelation(await p.Content.ReadAsStringAsync(ct)).ShouldBe(b1);

        // Submission is partitioned by account.
        var existing = new { pairingCode = code, deviceFingerprint = "AAAA-AAAA" };
        var unknown = new { pairingCode = "ZZZZ-ZZZZ", deviceFingerprint = "AAAA-AAAA" };
        (await reg1.SendAsync(HttpMethod.Post, Path, existing, ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        (await reg1.SendAsync(HttpMethod.Post, Path, unknown, ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        using var e = await reg1.SendAsync(HttpMethod.Post, Path, existing, ct);
        using var u = await reg1.SendAsync(HttpMethod.Post, Path, unknown, ct);
        e.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        u.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        RegistryTestHost.WithoutCorrelation(await e.Content.ReadAsStringAsync(ct)).ShouldBe(b1);
        RegistryTestHost.WithoutCorrelation(await u.Content.ReadAsStringAsync(ct)).ShouldBe(b1);

        using var other = await reg2.SendAsync(HttpMethod.Post, Path, unknown, ct);
        other.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
    }
}

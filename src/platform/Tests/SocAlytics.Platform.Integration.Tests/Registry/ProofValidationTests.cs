using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Registry.Abstractions;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Support;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class ProofValidationTests(PostgresContainerFixture postgres)
{
    private const string Base = "https://stamp.example.test";
    private const string Activation = Base + "/api/v1/analyst-managers/pairings/activation";
    private const string Token = Base + "/api/v1/analyst-managers/token";
    private const string SelfPath = "/api/v1/analyst-managers/self/registration";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class Harness(IsolatedDatabase db, ServiceProvider provider, FakeTimeProvider time) : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = time;
        public TestAnalystManager Manager { get; } = new();
        public IManagerProofValidator Validator => provider.GetRequiredService<IManagerProofValidator>();
        public IProofReplayStore Replay => provider.GetRequiredService<IProofReplayStore>();

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            await provider.DisposeAsync();
            await db.DisposeAsync();
        }
    }

    private async Task<Harness> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
            ["Stamp:Id"] = "fixture-stamp",
            ["Stamp:PublicBaseUri"] = Base,
        }).Build();
        var time = new FakeTimeProvider(Start);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<TimeProvider>(time);
        services.AddTestHostEnvironment();
        services.AddInfrastructure();
        services.AddApplication();
        return new Harness(db, services.BuildServiceProvider(), time);
    }

    private static string Refusal(ProofValidationResult r) => r.RefusalReason!;

    [Fact]
    public async Task ValidProofsOfEachKindAreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var now = h.Time.GetUtcNow();

        var a = await h.Validator.ValidateActivationProofAsync(h.Manager.ActivationProof(now, Activation, "abc"), h.Manager.PublicJwk, ct);
        a.Succeeded.ShouldBeTrue();
        a.Challenge.ShouldBe("abc");

        (await h.Validator.ValidateClientAssertionAsync(h.Manager.ClientAssertion(now, Token), h.Manager.ClientId, h.Manager.PublicJwk, ct))
            .Succeeded.ShouldBeTrue();

        var d = await h.Validator.ValidateDPoPProofAsync(
            h.Manager.DPoPProof(now, "GET", Base + SelfPath, "tok"), "GET", SelfPath + "?x=1", "tok", true, ct);
        d.Succeeded.ShouldBeTrue();
        d.KeyThumbprint.ShouldBe(h.Manager.Thumbprint);

        var noAth = await h.Validator.ValidateDPoPProofAsync(
            h.Manager.DPoPProof(now, "POST", Token), "POST", "/api/v1/analyst-managers/token", null, false, ct);
        noAth.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ActivationProofNegativeCases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var now = h.Time.GetUtcNow();
        var m = h.Manager;
        using var other = new TestAnalystManager();

        async Task<ProofValidationResult> Run(string proof) => await h.Validator.ValidateActivationProofAsync(proof, m.PublicJwk, ct);

        Refusal(await Run(m.ActivationProof(now, Activation, header: x => x["typ"] = "JWT"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now, Activation, header: x => x["alg"] = "none"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now, Activation, header: x => x["jwk"] = m.PublicJwkObject()))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now, Base + "/other"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now - TimeSpan.FromMinutes(5), Activation))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now + TimeSpan.FromMinutes(5), Activation))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ActivationProof(now, Activation, signer: other))).ShouldBe("proof-invalid");
        Refusal(await Run("not-a-jwt")).ShouldBe("proof-invalid");
    }

    [Fact]
    public async Task ClientAssertionNegativeCases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var now = h.Time.GetUtcNow();
        var m = h.Manager;
        using var other = new TestAnalystManager();

        async Task<ProofValidationResult> Run(string proof) =>
            await h.Validator.ValidateClientAssertionAsync(proof, m.ClientId, m.PublicJwk, ct);

        Refusal(await Run(m.ClientAssertion(now, Base + "/wrong"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now - TimeSpan.FromMinutes(5), Token))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, lifetime: TimeSpan.FromMinutes(5)))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, clientId: "someone-else"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, header: x => x["alg"] = "none"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, header: x => x["alg"] = "HS256"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, header: x => x["alg"] = "RS256"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.ClientAssertion(now, Token, signer: other))).ShouldBe("proof-invalid");
    }

    [Fact]
    public async Task DPoPNegativeCases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var now = h.Time.GetUtcNow();
        var m = h.Manager;
        using var other = new TestAnalystManager();

        async Task<ProofValidationResult> Run(string proof) =>
            await h.Validator.ValidateDPoPProofAsync(proof, "GET", SelfPath, "tok", true, ct);

        Refusal(await Run(m.DPoPProof(now, "GET", Base + SelfPath, "tok", header: x => x["typ"] = "JWT"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "GET", Base + SelfPath, "tok", header: x => x["alg"] = "none"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "POST", Base + SelfPath, "tok"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "GET", Base + "/other", "tok"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now - TimeSpan.FromMinutes(5), "GET", Base + SelfPath, "tok"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now + TimeSpan.FromMinutes(5), "GET", Base + SelfPath, "tok"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "GET", Base + SelfPath))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "GET", Base + SelfPath, "other-token"))).ShouldBe("proof-invalid");
        Refusal(await Run(m.DPoPProof(now, "GET", Base + SelfPath, "tok", header: x => x.Remove("jwk")))).ShouldBe("proof-invalid");
    }

    [Fact]
    public async Task ReplayedJtiIsRefusedPerKindAndKindsAreIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var now = h.Time.GetUtcNow();
        var m = h.Manager;
        const string jti = "same-jti";

        async Task<ProofValidationResult> Act() =>
            await h.Validator.ValidateActivationProofAsync(m.ActivationProof(now, Activation, jti: jti), m.PublicJwk, ct);
        async Task<ProofValidationResult> Assertion() =>
            await h.Validator.ValidateClientAssertionAsync(m.ClientAssertion(now, Token, jti: jti), m.ClientId, m.PublicJwk, ct);
        async Task<ProofValidationResult> DPoP() =>
            await h.Validator.ValidateDPoPProofAsync(m.DPoPProof(now, "GET", Base + SelfPath, jti: jti), "GET", SelfPath, null, false, ct);

        (await Act()).Succeeded.ShouldBeTrue();
        Refusal(await Act()).ShouldBe("proof-replayed");
        (await Assertion()).Succeeded.ShouldBeTrue();
        Refusal(await Assertion()).ShouldBe("proof-replayed");
        (await DPoP()).Succeeded.ShouldBeTrue();
        Refusal(await DPoP()).ShouldBe("proof-replayed");
    }

    [Fact]
    public async Task ReplayRowsPurgeAfterExpiry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await CreateAsync(ct);
        var hash = new byte[32];
        var expires = h.Time.GetUtcNow().AddMinutes(2);

        (await h.Replay.TryRecordAsync("dpop", hash, expires, ct)).ShouldBeTrue();
        (await h.Replay.TryRecordAsync("dpop", hash, expires, ct)).ShouldBeFalse();
        (await h.Replay.PurgeExpiredAsync(h.Time.GetUtcNow(), ct)).ShouldBe(0);
        (await h.Replay.PurgeExpiredAsync(expires.AddSeconds(1), ct)).ShouldBe(1);
        (await h.Replay.TryRecordAsync("dpop", hash, expires.AddMinutes(1), ct)).ShouldBeTrue();
    }
}

using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using SocAlytics.Platform.Domain.IdentityAccess;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class ExternalIdentityStoreTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<Guid> InsertAccountAsync(string connectionString, string name, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO socalytics.member_account (id, account_name, normalized_account_name, security_stamp, membership_changed_at, created_at) " +
            "VALUES (@id, @n, @n, 'stamp', now(), now())", c);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("n", name);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    private static async Task<string?> SqlStateAsync(string connectionString, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
        return ex.SqlState;
    }

    private static ExternalIdentityLink NewLink(Guid accountId, string provider, string issuer, string subject, Guid linkedBy) =>
        new(Guid.NewGuid(), accountId, provider, issuer, subject, Now, linkedBy);

    private static PendingExternalSignIn NewPending(string state, DateTimeOffset expiresAt, DateTimeOffset? createdAt = null) =>
        new(Guid.NewGuid(), SecretHash(state), SecretHash("binding-" + state), "nonce", "verifier", "club-idp", null, createdAt ?? Now, expiresAt);

    private static byte[] SecretHash(string value) =>
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));

    [Fact]
    public async Task LinkRoundTripsAndUniquenessRulesAreDistinctResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var alice = await InsertAccountAsync(db.AppConnectionString, "alice", ct);
        var bob = await InsertAccountAsync(db.AppConnectionString, "bob", ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalIdentityStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var first = NewLink(alice, "club-idp", "https://idp.example", "sub-1", alice);
        (await store.InsertAsync(first, ct)).ShouldBe(ExternalIdentityInsertOutcome.Inserted);

        // Same issuer and subject for another member.
        (await store.InsertAsync(NewLink(bob, "club-idp", "https://idp.example", "sub-1", alice), ct))
            .ShouldBe(ExternalIdentityInsertOutcome.IdentityAlreadyLinked);

        // Same member and provider with another identity.
        (await store.InsertAsync(NewLink(alice, "club-idp", "https://idp.example", "sub-2", alice), ct))
            .ShouldBe(ExternalIdentityInsertOutcome.AccountProviderAlreadyLinked);

        // The transaction stays usable and another provider is allowed.
        (await store.InsertAsync(NewLink(alice, "other-idp", "https://other.example", "sub-1", alice), ct))
            .ShouldBe(ExternalIdentityInsertOutcome.Inserted);

        var found = await store.FindByIssuerAndSubjectAsync("https://idp.example", "sub-1", ct);
        found.ShouldNotBeNull();
        found.Id.ShouldBe(first.Id);
        found.AccountId.ShouldBe(alice);
        found.Version.ShouldBe(1);
        (await store.FindByIssuerAndSubjectAsync("https://idp.example", "missing", ct)).ShouldBeNull();
        (await store.ListByAccountAsync(alice, ct)).Count.ShouldBe(2);
        (await store.ListByAccountAsync(bob, ct)).ShouldBeEmpty();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task DeleteRequiresTheMemberVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var alice = await InsertAccountAsync(db.AppConnectionString, "alice", ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IExternalIdentityStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var link = NewLink(alice, "club-idp", "https://idp.example", "sub-1", alice);
        await store.InsertAsync(link, ct);

        (await store.DeleteAsync(link.Id, 99, ct)).ShouldBeFalse();
        (await store.ListByAccountAsync(alice, ct)).Count.ShouldBe(1);
        (await store.DeleteAsync(link.Id, 1, ct)).ShouldBeTrue();
        (await store.ListByAccountAsync(alice, ct)).ShouldBeEmpty();
        (await store.DeleteAsync(link.Id, 1, ct)).ShouldBeFalse();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task PendingSignInIsConsumedOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPendingExternalSignInStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var pending = NewPending("state-1", DateTimeOffset.UtcNow.AddMinutes(5));
        await store.CreateAsync(pending, ct);

        var consumed = await store.ConsumeAsync(pending.StateHash, DateTimeOffset.UtcNow, ct);
        consumed.ShouldNotBeNull();
        consumed.Id.ShouldBe(pending.Id);
        consumed.Nonce.ShouldBe("nonce");
        consumed.CodeVerifier.ShouldBe("verifier");
        consumed.BrowserBindingHash.ShouldBe(pending.BrowserBindingHash);
        (await store.ConsumeAsync(pending.StateHash, DateTimeOffset.UtcNow, ct)).ShouldBeNull();
        (await store.ConsumeAsync(SecretHash("unknown"), DateTimeOffset.UtcNow, ct)).ShouldBeNull();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task ConcurrentConsumptionSucceedsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        var pending = NewPending("state-race", DateTimeOffset.UtcNow.AddMinutes(5));
        await using (var scope = provider.CreateAsyncScope())
        {
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            await scope.ServiceProvider.GetRequiredService<IPendingExternalSignInStore>().CreateAsync(pending, ct);
            await uow.CommitAsync(ct);
        }

        async Task<PendingExternalSignIn?> ConsumeAsync()
        {
            await using var scope = provider.CreateAsyncScope();
            await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
            var result = await scope.ServiceProvider.GetRequiredService<IPendingExternalSignInStore>()
                .ConsumeAsync(pending.StateHash, DateTimeOffset.UtcNow, ct);
            await uow.CommitAsync(ct);
            return result;
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(ConsumeAsync, ct)));
        results.Count(r => r is not null).ShouldBe(1);
    }

    [Fact]
    public async Task ExpiredPendingSignInCannotBeConsumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPendingExternalSignInStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        var pending = NewPending("state-expiring", Now.AddMinutes(5));
        await store.CreateAsync(pending, ct);

        (await store.ConsumeAsync(pending.StateHash, Now.AddMinutes(5), ct)).ShouldBeNull();
        (await store.ConsumeAsync(pending.StateHash, Now.AddMinutes(6), ct)).ShouldBeNull();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task DeletionRemovesAtMostOneHundredExpiredOrConsumedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IPendingExternalSignInStore>();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);

        for (var i = 0; i < 130; i++)
        {
            await store.CreateAsync(NewPending($"expired-{i}", Now.AddMinutes(-1), Now.AddMinutes(-6)), ct);
        }

        var consumed = NewPending("consumed", Now.AddMinutes(5));
        await store.CreateAsync(consumed, ct);
        (await store.ConsumeAsync(consumed.StateHash, Now, ct)).ShouldNotBeNull();
        var live = NewPending("live", Now.AddMinutes(5));
        await store.CreateAsync(live, ct);

        (await store.DeleteExpiredOrConsumedAsync(Now, 1000, ct)).ShouldBe(100);
        (await store.DeleteExpiredOrConsumedAsync(Now, 100, ct)).ShouldBe(31);
        (await store.DeleteExpiredOrConsumedAsync(Now, 100, ct)).ShouldBe(0);
        (await store.ConsumeAsync(live.StateHash, Now, ct)).ShouldNotBeNull();
        await uow.CommitAsync(ct);
    }

    [Fact]
    public async Task OneTimeCredentialProviderIdIsSetExactlyForIdentityLink()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var alice = await InsertAccountAsync(db.AppConnectionString, "alice", ct);

        const string insert =
            "INSERT INTO socalytics.one_time_credential (id, member_account_id, purpose, provider_id, credential_hash, issued_at, issued_by_account_id, expires_at) " +
            "VALUES (@id, @a, @purpose, @provider, @hash, now(), @a, now() + interval '1 hour')";

        async Task<string?> TryAsync(string purpose, string? provider)
        {
            await using var c = new NpgsqlConnection(db.AppConnectionString);
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(insert, c);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("a", alice);
            cmd.Parameters.AddWithValue("purpose", purpose);
            cmd.Parameters.AddWithValue("provider", (object?)provider ?? DBNull.Value);
            cmd.Parameters.AddWithValue("hash", Guid.NewGuid().ToByteArray());
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
                return null;
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        (await TryAsync("identity-link", "club-idp")).ShouldBeNull();
        (await TryAsync("set-password", null)).ShouldBeNull();
        (await TryAsync("password-reset", null)).ShouldBeNull();
        (await TryAsync("identity-link", null)).ShouldBe(PostgresErrorCodes.CheckViolation);
        (await TryAsync("set-password", "club-idp")).ShouldBe(PostgresErrorCodes.CheckViolation);
        (await TryAsync("identity-link", "Not Valid")).ShouldBe(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task ProviderIdentifiersMustBeUrlSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var alice = await InsertAccountAsync(db.AppConnectionString, "alice", ct);

        var state = await SqlStateAsync(
            db.AppConnectionString,
            "INSERT INTO socalytics.external_identity_link (id, account_id, provider_id, issuer, subject, linked_at, linked_by) " +
            "VALUES (gen_random_uuid(), @a, 'Bad_Id', 'i', 's', now(), @a)",
            ct,
            ("a", alice));
        state.ShouldBe(PostgresErrorCodes.CheckViolation);

        Should.Throw<ArgumentException>(() => NewLink(alice, "Bad_Id", "i", "s", alice));
        Should.Throw<ArgumentException>(() => NewLink(alice, new string('a', 33), "i", "s", alice));
    }

    [Fact]
    public async Task AuditDetailKeysProviderAndCauseArePersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestRequestContext>().ActorKind = AuditActorKind.System;
        var id = Guid.NewGuid().ToString();

        await using (var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordAsync(new AuditEvent
            {
                EventType = "session.external-sign-in-failed",
                Action = "external-sign-in",
                Outcome = AuditOutcome.Failed,
                Resource = new AuditResource("session", id),
                Details = new Dictionary<string, string> { ["provider"] = "club-idp", ["cause"] = "unlinked" },
            }, ct);
            await work.CommitAsync(ct);
        }

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM socalytics.security_audit_event WHERE resource_id = @i AND details->>'provider' = 'club-idp' AND details->>'cause' = 'unlinked'", c);
        cmd.Parameters.AddWithValue("i", id);
        ((long)(await cmd.ExecuteScalarAsync(ct))!).ShouldBe(1);
    }
}

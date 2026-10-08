using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class SecurityAuditTrailTests(PostgresContainerFixture postgres)
{
    private static AuditEvent NewEvent(IReadOnlyDictionary<string, string>? details = null) => new()
    {
        EventType = "member.role-assigned",
        Action = "assign-role",
        Outcome = AuditOutcome.Succeeded,
        Resource = new AuditResource("member", "abc"),
        ReasonCode = "ok",
        Details = details,
    };

    private async Task<IsolatedDatabase> MigratedAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static async Task<long> CountAsync(string connectionString, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM socalytics.security_audit_event", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string?> SqlStateAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(connectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        var ex = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(ct));
        return ex.SqlState;
    }

    [Fact]
    public async Task CommittedEventPersistsWithAllFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        var time = new MutableTimeProvider();
        await using var provider = PlatformServices.Build(db, time: time);
        await using var scope = provider.CreateAsyncScope();
        var actor = Guid.NewGuid();
        var session = Guid.NewGuid();
        var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
        context.ActorKind = AuditActorKind.Member;
        context.MemberAccountId = actor;
        context.SessionId = session;
        context.CorrelationId = "corr-1";

        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (var work = await uow.BeginAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IAuditTrail>()
                .RecordAsync(NewEvent(new Dictionary<string, string> { ["role"] = "registrar" }), ct);
            await work.CommitAsync(ct);
        }

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT occurred_at, event_type, action, outcome, actor_kind, actor_account_id, session_id, resource_type, " +
            "resource_id, data_class, reason_code, details->>'role', correlation_id FROM socalytics.security_audit_event", c);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        (await reader.ReadAsync(ct)).ShouldBeTrue();
        reader.GetFieldValue<DateTime>(0).ShouldBe(time.GetUtcNow().UtcDateTime);
        reader.GetString(1).ShouldBe("member.role-assigned");
        reader.GetString(2).ShouldBe("assign-role");
        reader.GetString(3).ShouldBe("succeeded");
        reader.GetString(4).ShouldBe("member");
        reader.GetGuid(5).ShouldBe(actor);
        reader.GetGuid(6).ShouldBe(session);
        reader.GetString(7).ShouldBe("member");
        reader.GetString(8).ShouldBe("abc");
        reader.GetString(9).ShouldBe("DAT-001");
        reader.GetString(10).ShouldBe("ok");
        reader.GetString(11).ShouldBe("registrar");
        reader.GetString(12).ShouldBe("corr-1");
    }

    [Fact]
    public async Task RolledBackUnitOfWorkLeavesNoEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();

        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (await uow.BeginAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordAsync(NewEvent(), ct);
        }

        (await CountAsync(db.AppConnectionString, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task RecordWithoutUnitOfWorkThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();

        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordAsync(NewEvent(), ct));
    }

    [Fact]
    public async Task IndependentRecordSurvivesRollback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();

        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using (await uow.BeginAsync(ct))
        {
            await scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordIndependentAsync(NewEvent(), ct);
        }

        (await CountAsync(db.AppConnectionString, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task DisallowedDetailsKeyIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();

        await Should.ThrowAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IAuditTrail>()
            .RecordIndependentAsync(NewEvent(new Dictionary<string, string> { ["note"] = "free text" }), ct));
    }

    [Fact]
    public async Task RuntimeRoleCannotMutateAndMigratorIsStoppedByTrigger()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordIndependentAsync(NewEvent(), ct);

        foreach (var sql in new[]
        {
            "UPDATE socalytics.security_audit_event SET action = 'x'",
            "DELETE FROM socalytics.security_audit_event",
            "TRUNCATE socalytics.security_audit_event",
        })
        {
            (await SqlStateAsync(db.AppConnectionString, sql, ct)).ShouldBe("42501");
        }

        foreach (var sql in new[]
        {
            "UPDATE socalytics.security_audit_event SET action = 'x'",
            "DELETE FROM socalytics.security_audit_event",
        })
        {
            (await SqlStateAsync(db.MigratorConnectionString, sql, ct)).ShouldBe("P0001");
        }
    }

    [Fact]
    public async Task ActorKindAndOutcomeConstraintsAreNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await MigratedAsync(ct);

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_constraint WHERE conrelid = 'socalytics.security_audit_event'::regclass " +
            "AND conname IN ('ck_security_audit_event_outcome', 'ck_security_audit_event_actor_kind')", c);
        ((long)(await cmd.ExecuteScalarAsync(ct))!).ShouldBe(2);

        var state = await SqlStateAsync(db.AppConnectionString,
            "INSERT INTO socalytics.security_audit_event (id, occurred_at, event_type, action, outcome, actor_kind, " +
            $"resource_type, correlation_id) VALUES ('{Guid.NewGuid()}', now(), 'e', 'a', 'succeeded', 'robot', 'member', 'c')", ct);
        state.ShouldBe("23514");
    }
}

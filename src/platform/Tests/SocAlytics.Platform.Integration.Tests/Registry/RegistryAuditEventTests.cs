using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistryAuditEventTests(PostgresContainerFixture postgres)
{
    private static readonly Guid Id = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<string, string> Details = new Dictionary<string, string>
    {
        ["managerId"] = "m",
        ["stampId"] = "s",
        ["registrationCorrelationId"] = "c",
        ["deviceFingerprint"] = "ABCD-EFGH",
    };

    public static TheoryData<string, AuditEvent, string, string, string> Events => new()
    {
        { "analyst-manager.pairing-issued", RegistryAuditEvents.PairingIssued(Id, Details), "pair", "succeeded", "anonymous" },
        { "analyst-manager.pairing-refused", RegistryAuditEvents.PairingRefused(Id, "software-key-not-allowed", Details), "pair", "refused", "anonymous" },
        { "analyst-manager.pending-request-created", RegistryAuditEvents.PendingRequestCreated(Id, Details), "submit", "succeeded", "member" },
        { "analyst-manager.submission-refused", RegistryAuditEvents.SubmissionRefused(Id, "fingerprint-mismatch", Details), "submit", "refused", "member" },
        { "analyst-manager.approved", RegistryAuditEvents.Approved(Id, Details), "approve", "succeeded", "member" },
        { "analyst-manager.rejected", RegistryAuditEvents.Rejected(Id, Details), "reject", "succeeded", "member" },
        { "analyst-manager.expired", RegistryAuditEvents.Expired(Id, "pairing-window-elapsed", Details), "expire", "succeeded", "system" },
        { "analyst-manager.activation-refused", RegistryAuditEvents.ActivationRefused(Id, "proof-invalid", Details), "activate", "refused", "anonymous" },
        { "analyst-manager.activated", RegistryAuditEvents.Activated(Id, Details), "activate", "succeeded", "anonymous" },
        { "analyst-manager.credential-issued", RegistryAuditEvents.CredentialIssued(Id, Details), "issue-credential", "succeeded", "analyst-manager" },
        { "analyst-manager.credential-refused", RegistryAuditEvents.CredentialRefused(Id, "registration-inactive", Details), "issue-credential", "refused", "analyst-manager" },
        { "analyst-manager.unregistration-started", RegistryAuditEvents.UnregistrationStarted(Id, Details), "begin-unregister", "succeeded", "analyst-manager" },
        { "analyst-manager.unregistered", RegistryAuditEvents.Unregistered(Id, Details), "unregister", "succeeded", "analyst-manager" },
        { "analyst-manager.revoked", RegistryAuditEvents.Revoked(Id, Details), "revoke", "succeeded", "member" },
    };

    [Theory]
    [MemberData(nameof(Events))]
    public async Task FactoryEventPersistsInUnitOfWorkAndIndependently(
        string eventType, AuditEvent auditEvent, string action, string outcome, string actorKind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TestRequestContext>();
        context.ActorKind = AuditActorKind.Member;
        context.MemberAccountId = null;
        context.CorrelationId = "corr";
        var trail = scope.ServiceProvider.GetRequiredService<IAuditTrail>();

        await using (var work = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct))
        {
            await trail.RecordAsync(auditEvent, ct);
            await work.CommitAsync(ct);
        }

        await trail.RecordIndependentAsync(auditEvent, ct);

        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT action, outcome, actor_kind, resource_type, resource_id, data_class, reason_code, details->>'managerId', " +
            "details->>'registrationCorrelationId' FROM socalytics.security_audit_event WHERE event_type = @t", c);
        cmd.Parameters.AddWithValue("t", eventType);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = 0;
        while (await reader.ReadAsync(ct))
        {
            rows++;
            reader.GetString(0).ShouldBe(action);
            reader.GetString(1).ShouldBe(outcome);
            reader.GetString(2).ShouldBe(actorKind);
            reader.GetString(3).ShouldBe("analyst-manager-registration");
            reader.GetString(4).ShouldBe(Id.ToString());
            reader.GetString(5).ShouldBe("DAT-001");
            (reader.IsDBNull(6) ? null : reader.GetString(6)).ShouldBe(auditEvent.ReasonCode);
            reader.GetString(7).ShouldBe("m");
            reader.GetString(8).ShouldBe("c");
        }

        rows.ShouldBe(2);
    }

    [Fact]
    public void DisallowedDetailKeyIsRejected()
    {
        Should.Throw<ArgumentException>(() =>
            RegistryAuditEvents.Approved(Id, new Dictionary<string, string> { ["hostLabel"] = "x" }));
    }

    [Fact]
    public async Task TrailRejectsDisallowedDetailKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode
            .ShouldBe(MigratorExitCode.Success);
        await using var provider = PlatformServices.Build(db);
        await using var scope = provider.CreateAsyncScope();
        var bad = RegistryAuditEvents.Approved(Id) with
        {
            Details = new Dictionary<string, string> { ["hostLabel"] = "x" },
        };

        await Should.ThrowAsync<ArgumentException>(() =>
            scope.ServiceProvider.GetRequiredService<IAuditTrail>().RecordIndependentAsync(bad, ct));
    }
}

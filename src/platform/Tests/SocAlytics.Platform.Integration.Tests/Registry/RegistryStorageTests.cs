using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure;
using SocAlytics.Platform.Infrastructure.Persistence;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Integration.Tests.Registry.Domain;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistryStorageTests(PostgresContainerFixture postgres)
{
    private static readonly DateTimeOffset Now = RegistryDomainFixtures.Now;

    private async Task<(IsolatedDatabase Db, ServiceProvider Provider)> CreateAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{DatabaseConnectionNames.Runtime}"] = db.AppConnectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddTestHostEnvironment();
        services.AddInfrastructure();
        services.AddApplication();
        return (db, services.BuildServiceProvider());
    }

    private static StoredCapabilityDeclaration StoredDeclaration(CapabilityDeclaration d) => new(
        d, "analysts/tracking", "sha-256:" + new string('a', 64), ManifestJson(d), "image", Now, Guid.CreateVersion7());

    private static string ManifestJson(CapabilityDeclaration d) =>
        $$"""
        {
          "manifestVersion": "1.0.0",
          "capability": { "id": "{{d.CapabilityId}}", "version": "{{d.CapabilityVersion}}", "tier": "{{AnalysisPersistedNames.ToPersistedName(d.Tier)}}", "executionScope": "{{AnalysisPersistedNames.ToPersistedName(d.ExecutionScope)}}" },
          "upstream": [{{string.Join(",", d.Upstream.Select(u => $$"""{ "capabilityId": "{{u.CapabilityId}}", "capabilityVersion": "{{u.CapabilityVersion}}", "schemaVersion": "{{u.SchemaVersion}}", "kind": "{{AnalysisPersistedNames.ToPersistedName(u.Kind)}}" }"""))}}],
          "videoAccess": "{{d.VideoAccess}}",
          "output": { "schemaVersion": "{{d.OutputSchemaVersion}}" },
          "compatibleProfiles": [],
          "model": { "digest": "{{d.ModelDigest}}" },
          "runtime": { "constraints": ["cuda"], "accelerators": ["gpu"] },
          "resources": { "memory": "8Gi" },
          "requirements": { "minMaxAttempts": 2, "minLeaseSeconds": 60, "minStaleTimeoutSeconds": 120, "minExecutionTimeoutSeconds": 300 }
        }
        """;

    private static async Task<AsyncServiceScope> BeginAsync(ServiceProvider provider, CancellationToken ct)
    {
        var scope = provider.CreateAsyncScope();
        return await Task.FromResult(scope);
    }

    private static async Task InTransactionAsync(AsyncServiceScope scope, Func<Task> action, CancellationToken ct)
    {
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().BeginAsync(ct);
        await action();
        await uow.CommitAsync(ct);
    }

    private static async Task<string?> SqlStateAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct))).SqlState;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task DeclarationRoundTripsAndRefusesDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = await BeginAsync(provider, ct);
        var store = scope.ServiceProvider.GetRequiredService<ICapabilityDeclarationStore>();
        var declaration = RegistryDomainFixtures.Declaration(
            "pressing", "2.0.1", ExecutionScope.Match, "forbidden", "pressing-v2",
            new DeclaredUpstream("tracking", "1.0.0", "tracking-v1", DependencyKind.Optional));
        var stored = StoredDeclaration(declaration);

        await InTransactionAsync(scope, async () =>
        {
            (await store.TryInsertAsync(stored, ct)).ShouldBeTrue();
            (await store.TryInsertAsync(stored, ct)).ShouldBeFalse();
        }, ct);

        var loaded = (await store.FindAsync(declaration.ImageDigest, ct))!;
        var d = loaded.Declaration;
        (d.ImageDigest, d.CapabilityId, d.CapabilityVersion, d.Tier, d.ExecutionScope, d.ModelDigest, d.VideoAccess, d.OutputSchemaVersion)
            .ShouldBe((declaration.ImageDigest, declaration.CapabilityId, declaration.CapabilityVersion, declaration.Tier,
                declaration.ExecutionScope, declaration.ModelDigest, declaration.VideoAccess, declaration.OutputSchemaVersion));
        d.RuntimeConstraints.ShouldBe(declaration.RuntimeConstraints);
        d.Accelerators.ShouldBe(declaration.Accelerators);
        d.Resources.ShouldBe(declaration.Resources);
        d.Upstream.ShouldBe(declaration.Upstream);
        d.Requirements.ShouldBe(declaration.Requirements);
        loaded.Repository.ShouldBe(stored.Repository);
        loaded.ManifestDigest.ShouldBe(stored.ManifestDigest);
        loaded.Source.ShouldBe("image");
        loaded.RegisteredAt.ShouldBe(Now);
        loaded.RegisteredBy.ShouldBe(stored.RegisteredBy);
        (await store.ListAsync(ct)).Count.ShouldBe(1);
        (await store.FindAsync("sha256:" + new string('9', 64), ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ProfileRoundTripsAndTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = await BeginAsync(provider, ct);
        var declarations = scope.ServiceProvider.GetRequiredService<ICapabilityDeclarationStore>();
        var profiles = scope.ServiceProvider.GetRequiredService<IAnalystProfileStore>();
        var declaration = RegistryDomainFixtures.Declaration();
        var draft = AnalystProfile.CreateDraft("tracking-gpu", 1, RegistryDomainFixtures.Content(declaration), RegistryDomainFixtures.Creator, Now);

        await InTransactionAsync(scope, async () =>
        {
            await declarations.TryInsertAsync(StoredDeclaration(declaration), ct);
            (await profiles.TryInsertAsync(draft, ct)).ShouldBeTrue();
            (await profiles.TryInsertAsync(draft, ct)).ShouldBeFalse();
        }, ct);

        var loaded = (await profiles.FindAsync("tracking-gpu", 1, ct))!;
        loaded.Version.ShouldBe(1);
        loaded.Profile.State.ShouldBe(ProfileState.Draft);
        loaded.Profile.Digest.ShouldBe(draft.Digest);
        loaded.Profile.Content.ComputeDigest().ShouldBe(draft.Digest);
        loaded.Profile.Content.Resources.ShouldBe(draft.Content.Resources);
        loaded.Profile.Content.InputSchemaVersions.ShouldBe(draft.Content.InputSchemaVersions);
        (await profiles.FindByDigestAsync(draft.Digest.ToString(), ct))!.Profile.ProfileId.ShouldBe("tracking-gpu");

        var approved = loaded.Profile.Approve(RegistryDomainFixtures.Approver, Now.AddMinutes(1));
        await InTransactionAsync(scope, async () =>
        {
            var stale = await profiles.UpdateStateAsync(approved, 99, ct);
            stale.Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
            var applied = await profiles.UpdateStateAsync(approved, loaded.Version, ct);
            applied.Outcome.ShouldBe(VersionedWriteOutcome.Applied);
            applied.Version.ShouldBe(2);
        }, ct);

        var reloaded = (await profiles.FindAsync("tracking-gpu", 1, ct))!;
        reloaded.Profile.State.ShouldBe(ProfileState.Approved);
        reloaded.Profile.ApprovedBy.ShouldBe(RegistryDomainFixtures.Approver);
        reloaded.Profile.ApprovedAt.ShouldBe(Now.AddMinutes(1));
        reloaded.Version.ShouldBe(2);
        (await profiles.ListAsync(ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task WorkflowAndDefaultRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = await BeginAsync(provider, ct);
        var store = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>();
        var definition = new StoredWorkflowDefinition("default", "1", "sha-256:" + new string('b', 64), "{\"nodes\":[]}", Now, Guid.CreateVersion7());

        await InTransactionAsync(scope, async () =>
        {
            (await store.TryInsertAsync(definition, ct)).ShouldBeTrue();
            (await store.TryInsertAsync(definition, ct)).ShouldBeFalse();
            (await store.TryInsertAsync(definition with { WorkflowVersion = "2" }, ct)).ShouldBeFalse("the digest is unique");
        }, ct);

        var loaded = (await store.FindAsync("default", "1", ct))!;
        loaded.DefinitionDigest.ShouldBe(definition.DefinitionDigest);
        loaded.CreatedAt.ShouldBe(Now);
        (await store.ListAsync(ct)).Count.ShouldBe(1);
        (await store.GetDefaultAsync(ct)).ShouldBeNull();

        await InTransactionAsync(scope, async () =>
        {
            var first = await store.SetDefaultAsync("default", "1", definition.CreatedBy, Now, null, ct);
            first.Outcome.ShouldBe(VersionedWriteOutcome.Applied);
            (await store.SetDefaultAsync("default", "1", null, Now, null, ct)).Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
            (await store.SetDefaultAsync("default", "1", null, Now, 99, ct)).Outcome.ShouldBe(VersionedWriteOutcome.ConcurrencyConflict);
            var second = await store.SetDefaultAsync("default", "1", null, Now.AddMinutes(1), first.Version, ct);
            second.Outcome.ShouldBe(VersionedWriteOutcome.Applied);
            second.Version.ShouldBe(first.Version + 1);
        }, ct);

        var current = (await store.GetDefaultAsync(ct))!;
        current.WorkflowId.ShouldBe("default");
        current.ChangedAt.ShouldBe(Now.AddMinutes(1));
        current.ChangedBy.ShouldBeNull();
    }

    [Fact]
    public async Task TriggersRefuseUpdatesAndDeletesOfImmutableTables()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = await BeginAsync(provider, ct);
        var declaration = RegistryDomainFixtures.Declaration();
        var workflow = new StoredWorkflowDefinition("default", "1", "sha-256:" + new string('b', 64), "{}", Now, null);
        await InTransactionAsync(scope, async () =>
        {
            await scope.ServiceProvider.GetRequiredService<ICapabilityDeclarationStore>().TryInsertAsync(StoredDeclaration(declaration), ct);
            await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionStore>().TryInsertAsync(workflow, ct);
        }, ct);

        // The migrator owns the tables, so only the triggers (not the app role's revoked privileges) refuse these statements.
        foreach (var sql in new[]
        {
            "UPDATE socalytics.analyst_capability_declaration SET repository = 'other'",
            "DELETE FROM socalytics.analyst_capability_declaration",
            "UPDATE socalytics.workflow_definition SET definition = '{\"x\":1}'",
            "DELETE FROM socalytics.workflow_definition",
        })
        {
            (await SqlStateAsync(db.MigratorConnectionString, sql, ct)).ShouldBe("P0001", sql);
            (await SqlStateAsync(db.AppConnectionString, sql, ct)).ShouldBe("42501", sql);
        }
    }

    [Fact]
    public async Task TriggersRefuseContentChangesOfNonDraftProfilesAndInvalidTransitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (db, provider) = await CreateAsync(ct);
        await using var _ = db;
        await using var __ = provider;
        await using var scope = await BeginAsync(provider, ct);
        var declaration = RegistryDomainFixtures.Declaration();
        var content = RegistryDomainFixtures.Content(declaration);
        await InTransactionAsync(scope, async () =>
        {
            await scope.ServiceProvider.GetRequiredService<ICapabilityDeclarationStore>().TryInsertAsync(StoredDeclaration(declaration), ct);
            var profiles = scope.ServiceProvider.GetRequiredService<IAnalystProfileStore>();
            foreach (var id in new[] { "draft", "approved", "retired" })
            {
                await profiles.TryInsertAsync(
                    AnalystProfile.CreateDraft(id, 1, content with { MaxAttempts = 3 + id.Length }, RegistryDomainFixtures.Creator, Now), ct);
            }
        }, ct);

        var app = db.AppConnectionString;
        await ExecuteAsync(app, "UPDATE socalytics.analyst_profile SET state = 'approved' WHERE profile_id = 'approved'", ct);
        await ExecuteAsync(app, "UPDATE socalytics.analyst_profile SET state = 'retired' WHERE profile_id = 'retired'", ct);

        // Draft content may change.
        await ExecuteAsync(app, "UPDATE socalytics.analyst_profile SET content = '{\"a\":1}' WHERE profile_id = 'draft'", ct);

        foreach (var id in new[] { "approved", "retired" })
        {
            (await SqlStateAsync(app, $"UPDATE socalytics.analyst_profile SET content = '{{\"a\":1}}' WHERE profile_id = '{id}'", ct)).ShouldBe("P0001");
            (await SqlStateAsync(app, $"UPDATE socalytics.analyst_profile SET profile_digest = 'x' WHERE profile_id = '{id}'", ct)).ShouldBe("P0001");
        }

        foreach (var sql in new[]
        {
            "UPDATE socalytics.analyst_profile SET state = 'draft' WHERE profile_id = 'approved'",
            "UPDATE socalytics.analyst_profile SET state = 'approved' WHERE profile_id = 'retired'",
            "UPDATE socalytics.analyst_profile SET state = 'draft' WHERE profile_id = 'retired'",
        })
        {
            (await SqlStateAsync(app, sql, ct)).ShouldBe("P0001", sql);
        }

        (await SqlStateAsync(app, "DELETE FROM socalytics.analyst_profile WHERE profile_id = 'draft'", ct)).ShouldBe("P0001");

        // A valid transition of an approved profile still works.
        await ExecuteAsync(app, "UPDATE socalytics.analyst_profile SET state = 'retired' WHERE profile_id = 'approved'", ct);
    }
}

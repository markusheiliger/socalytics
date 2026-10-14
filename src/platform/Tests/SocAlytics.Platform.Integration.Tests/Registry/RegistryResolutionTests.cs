using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using SocAlytics.Platform.Migrator;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry;

public sealed class RegistryResolutionTests(PostgresContainerFixture postgres)
{
    private sealed class FixedEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SocAlytics.Platform.Api";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private async Task<IsolatedDatabase> CreateDatabaseAsync(CancellationToken ct)
    {
        var db = await postgres.CreateDatabaseAsync(ct);
        (await MigratorHarness.RunAsync(db, TestMigrationCatalogs.Platform(), ct)).ExitCode.ShouldBe(MigratorExitCode.Success);
        return db;
    }

    private static PlatformApiFactory Start(IsolatedDatabase db, string? environment = null, bool omitWorkflow = false) =>
        new(db, configureServices: environment is null ? null : services => services.AddSingleton<IHostEnvironment>(new FixedEnvironment(environment)), omitWorkflow: omitWorkflow);

    private static async Task<WorkflowResolution> ResolveAsync(PlatformApiFactory factory, CancellationToken ct)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitionResolver>().ResolveDefaultAsync(ct);
    }

    private static async Task<long> CountAsync(IsolatedDatabase db, string sql, CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(db.AppConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Development_seed_populates_an_empty_registry_and_resolves_the_configured_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var factory = Start(db);
        var definition = (await ResolveAsync(factory, ct)).ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;

        definition.WorkflowId.ShouldBe("development-default");
        definition.WorkflowVersion.ShouldBe("1");
        definition.Nodes.Count.ShouldBe(2);
        definition.RequestedCapabilities.Count.ShouldBe(2);
        definition.SegmentationPolicy.DurationSeconds.ShouldBe(300);
        var segment = definition.Nodes.Single(n => n.ExecutionScope == ExecutionScope.Segment);
        segment.ImageDigest.ToString().ShouldBe("sha-256:" + new string('3', 64));
        segment.ModelDigest.ShouldBeNull();
        segment.MaxAttempts.ShouldBe(3);
        segment.Resources["cpu"].ShouldBe("1");
        var match = definition.Nodes.Single(n => n.ExecutionScope == ExecutionScope.Match);
        match.ModelDigest!.ToString().ShouldBe("sha-256:" + new string('6', 64));
        definition.Edges.ShouldHaveSingleItem().Kind.ShouldBe("required");

        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration WHERE source = 'seed'", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_profile WHERE state = 'approved'", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.default_workflow", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.seeded' AND actor_kind = 'system'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Seed_applies_only_to_an_empty_registry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        WorkflowResolution first;
        await using (var factory = Start(db))
        {
            first = await ResolveAsync(factory, ct);
        }

        await using var again = Start(db);
        var second = await ResolveAsync(again, ct);

        second.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition.Nodes.Select(n => n.AnalystProfileDigest)
            .ShouldBe(first.ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition.Nodes.Select(n => n.AnalystProfileDigest));
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(2);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.workflow_definition", ct)).ShouldBe(1);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.seeded'", ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Production_is_not_seeded_and_has_no_default_workflow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var factory = Start(db, "Production");
        var result = await ResolveAsync(factory, ct);

        result.ShouldBeOfType<WorkflowResolution.UnavailableResult>().Category.ShouldBe(WorkflowResolution.NotConfigured);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.analyst_capability_declaration", ct)).ShouldBe(0);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.security_audit_event WHERE event_type = 'registry.seeded'", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Missing_configuration_leaves_the_registry_empty_and_not_configured()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var factory = Start(db, omitWorkflow: true);
        var result = await ResolveAsync(factory, ct);

        result.ShouldBeOfType<WorkflowResolution.UnavailableResult>().Category.ShouldBe(WorkflowResolution.NotConfigured);
        (await CountAsync(db, "SELECT count(*) FROM socalytics.workflow_definition", ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Retired_profile_makes_the_default_invalid()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var factory = Start(db);
        (await ResolveAsync(factory, ct)).ShouldBeOfType<WorkflowResolution.ResolvedResult>();

        await using (var c = new NpgsqlConnection(db.AppConnectionString))
        {
            await c.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(
                "UPDATE socalytics.analyst_profile SET state = 'retired', retired_at = now(), retired_by = created_by WHERE profile_id = 'dev-profile' AND profile_version = 1", c);
            (await cmd.ExecuteNonQueryAsync(ct)).ShouldBe(1);
        }

        (await ResolveAsync(factory, ct)).ShouldBeOfType<WorkflowResolution.UnavailableResult>()
            .Category.ShouldBe(WorkflowResolution.InvalidDefinition);
    }

    [Fact]
    public async Task Resolution_is_stable_for_an_unchanged_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await CreateDatabaseAsync(ct);
        await using var factory = Start(db);
        var a = (await ResolveAsync(factory, ct)).ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;
        var b = (await ResolveAsync(factory, ct)).ShouldBeOfType<WorkflowResolution.ResolvedResult>().Definition;

        b.Nodes.Select(n => (n.NodeKey, n.AnalystProfileDigest, n.ImageDigest)).ShouldBe(a.Nodes.Select(n => (n.NodeKey, n.AnalystProfileDigest, n.ImageDigest)));
    }
}

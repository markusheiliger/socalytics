using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog;
using SocAlytics.Platform.Application.AgentOrchestration.Catalog.Registration;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;
using SocAlytics.Platform.Infrastructure.Workers;

namespace SocAlytics.Platform.Infrastructure.AgentOrchestration.Catalog;

/// <summary>
/// Seeds an empty Agent Catalog with the two baseline Specialist Agents (Development and Test only): the same validator and
/// stores as the registration handlers, without member authorization, as the system actor. No Coach Agent artifact is seeded.
/// </summary>
internal sealed class AgentCatalogSeeder(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IConfiguration configuration,
    HealthCheckService health,
    TimeProvider time,
    ILogger<AgentCatalogSeeder> logger) : BackgroundService
{
    internal const string SeedActor = "system:seed";
    internal const string SeedVersion = "0.1.0";

    private const string ResourcePrefix = "AgentCatalogSeed/";

    private static readonly (string AgentId, string DisplayName, string Prompt, string Tools, string Safety)[] Agents =
    [
        ("specialist-tactics", "Tactics Specialist", "specialist-tactics-prompt", "specialist-tactics-tools", "specialist-tactics-safety"),
        ("specialist-video-analysis", "Video Analysis Specialist", "specialist-video-analysis-prompt", "specialist-video-analysis-tools", "specialist-video-analysis-safety"),
    ];

    private bool _done;

    // The switch (default on) lets a host or test that needs an empty catalog skip the seed.
    private bool Enabled =>
        (environment.IsDevelopment() || environment.IsEnvironment("Test"))
        && configuration.GetValue("AgentOrchestration:Catalog:SeedEnabled", true);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if ((environment.IsDevelopment() || environment.IsEnvironment("Test"))
            && configuration.GetSection("AgentOrchestration:Agents:Enabled").Exists())
        {
            logger.LogWarning(
                "AgentOrchestration:Agents:Enabled is no longer used and is ignored; enable agent versions through the Agent Catalog");
        }

        if (Enabled)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await SeedAsync(limit.Token);
                _done = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("The Agent Catalog seed is deferred: {Category}", ex.GetType().Name);
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled)
        {
            return;
        }

        while (!_done && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WorkerLoop.WaitForDatabaseAsync(health, time, stoppingToken);
                await SeedAsync(stoppingToken);
                _done = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("The Agent Catalog seed failed: {Category}; retrying", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            }
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var unit = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken);
        var artifacts = services.GetRequiredService<IAgentCatalogArtifactStore>();
        if (await artifacts.AnyAsync(cancellationToken))
        {
            return;
        }

        var registrar = services.GetRequiredService<AgentCatalogRegistrar>();
        var versions = services.GetRequiredService<IAgentVersionStore>();
        var enablements = services.GetRequiredService<IAgentEnablementStore>();
        var audit = services.GetRequiredService<IAuditTrail>();

        var modelSelection = await RegisterArtifactAsync(registrar, AgentCatalogRecordKind.ModelSelection, "simulated-model", cancellationToken);
        if (modelSelection is null)
        {
            return;
        }

        foreach (var (agentId, displayName, prompt, tools, safety) in Agents)
        {
            var references = new[]
            {
                (Property: "promptTemplate", Record: await RegisterArtifactAsync(registrar, AgentCatalogRecordKind.PromptTemplate, prompt, cancellationToken)),
                (Property: "modelSelection", Record: modelSelection),
                (Property: "toolPolicy", Record: await RegisterArtifactAsync(registrar, AgentCatalogRecordKind.ToolPolicy, tools, cancellationToken)),
                (Property: "safetyPolicy", Record: await RegisterArtifactAsync(registrar, AgentCatalogRecordKind.SafetyPolicy, safety, cancellationToken)),
            };
            if (references.Any(r => r.Record is null))
            {
                return;
            }

            var artifactReferences = new JsonObject();
            foreach (var (property, record) in references)
            {
                artifactReferences[property] = new JsonObject
                {
                    ["id"] = record!.Id,
                    ["version"] = record.Version,
                    ["digest"] = record.Digest,
                };
            }

            var document = new JsonObject
            {
                ["schemaVersion"] = "1.0.0",
                ["kind"] = AgentCatalogRecordValidator.AgentVersionKind,
                ["agentId"] = agentId,
                ["group"] = AgentCatalogIds.GroupSpecialist,
                ["displayName"] = displayName,
                ["artifacts"] = artifactReferences,
            };
            using var parsed = JsonDocument.Parse(document.ToJsonString());
            var registered = await registrar.RegisterAgentVersionAsync(SeedVersion, parsed.RootElement.Clone(), SeedActor, true, cancellationToken);
            if (!registered.IsRegistered)
            {
                logger.LogWarning("The Agent Catalog seed of {AgentId} was refused; the catalog is not seeded", agentId);
                return;
            }

            var version = registered.Record!;
            var approval = new AgentVersionApproval(SeedActor, time.GetUtcNow(), false, false);
            var approved = await versions.ApproveAsync(agentId, SeedVersion, approval, version.Version, cancellationToken);
            if (approved.Outcome != VersionedWriteOutcome.Applied)
            {
                return;
            }

            await audit.RecordAsync(AgentCatalogAuditEvents.VersionApproved(agentId, SeedVersion, version.Digest, false, false, true), cancellationToken);
            AgentCatalogMetrics.ApprovalRecorded();

            var enablement = await enablements.FindAsync(agentId, false, cancellationToken);
            if (enablement is null)
            {
                return;
            }

            var enabled = await enablements.SetAsync(agentId, SeedVersion, SeedActor, time.GetUtcNow(), enablement.Version, cancellationToken);
            if (enabled.Outcome != VersionedWriteOutcome.Applied)
            {
                return;
            }

            await audit.RecordAsync(AgentCatalogAuditEvents.EnablementChanged(agentId, enablement.AgentVersion, SeedVersion, true), cancellationToken);
            AgentCatalogMetrics.EnablementChanged();
        }

        await unit.CommitAsync(cancellationToken);
        logger.LogInformation("Seeded the Agent Catalog with the baseline Specialist Agents");
    }

    private async Task<AgentCatalogArtifactRecord?> RegisterArtifactAsync(
        AgentCatalogRegistrar registrar, AgentCatalogRecordKind kind, string name, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(ReadResource(name));
        var result = await registrar.RegisterArtifactAsync(kind, document.RootElement.Clone(), SeedActor, true, cancellationToken);
        if (!result.IsRegistered)
        {
            logger.LogWarning(
                "The Agent Catalog seed document {Name} was refused ({Codes}); the catalog is not seeded",
                name, string.Join(", ", result.Violations.Select(v => v.Code)));
        }

        return result.Record;
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(AgentCatalogSeeder).Assembly.GetManifestResourceStream($"{ResourcePrefix}{name}.json")
            ?? throw new InvalidOperationException($"The embedded seed document '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

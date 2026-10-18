using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Registry;
using SocAlytics.Platform.Application.Registry.Images;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Domain.Registry;
using SocAlytics.Platform.Infrastructure.Workers;

namespace SocAlytics.Platform.Infrastructure.Registry;

/// <summary>
/// Seeds an empty Registry from the configured development workflow (Development and Test only), without any image registry access.
/// Declarations are marked <c>source = seed</c> and profiles are approved.
/// </summary>
internal sealed partial class ConfiguredWorkflowSeeder(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    IOptionsMonitor<ConfiguredWorkflowOptions> options,
    IAnalystManifestValidator manifestValidator,
    HealthCheckService health,
    TimeProvider time,
    ILogger<ConfiguredWorkflowSeeder> logger) : BackgroundService
{
    private const string SeedRepository = "seed";
    private static readonly Guid SeedActor = Guid.Empty;

    private bool _done;

    private bool Enabled => environment.IsDevelopment() || environment.IsEnvironment("Test");

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Enabled)
        {
            // Seed before the host reports started when the database is ready; otherwise the background loop retries.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                await SeedAsync(limit.Token);
                _done = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("The Registry seed is deferred: {Category}", ex.GetType().Name);
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
                logger.LogWarning("The Registry seed failed: {Category}; retrying", ex.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
            }
        }
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var configured = options.CurrentValue;
        if (string.IsNullOrWhiteSpace(configured.WorkflowId) || configured.Nodes.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await using var unit = await services.GetRequiredService<IUnitOfWork>().BeginAsync(cancellationToken);
        var declarations = services.GetRequiredService<ICapabilityDeclarationStore>();
        var profiles = services.GetRequiredService<IAnalystProfileStore>();
        var workflows = services.GetRequiredService<IWorkflowDefinitionStore>();

        if ((await declarations.ListAsync(cancellationToken)).Count > 0
            || (await profiles.ListAsync(cancellationToken)).Count > 0
            || (await workflows.ListAsync(cancellationToken)).Count > 0
            || await workflows.GetDefaultAsync(cancellationToken) is not null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var definitionJson = await InsertAsync(configured, declarations, profiles, now, cancellationToken);
        if (definitionJson is null)
        {
            return;
        }

        var digest = ContentDigest.OfCanonical(definitionJson).ToString();
        var stored = new StoredWorkflowDefinition(configured.WorkflowId, configured.WorkflowVersion, digest, definitionJson, now, null);
        if (!await workflows.TryInsertAsync(stored, cancellationToken)
            || (await workflows.SetDefaultAsync(configured.WorkflowId, configured.WorkflowVersion, null, now, null, cancellationToken)).Outcome
                != VersionedWriteOutcome.Applied)
        {
            return;
        }

        await services.GetRequiredService<IAuditTrail>().RecordAsync(
            RegistryAuditEvents.RegistrySeeded(new Dictionary<string, string>
            {
                ["workflowId"] = configured.WorkflowId,
                ["workflowVersion"] = configured.WorkflowVersion,
                ["definitionDigest"] = digest,
            }),
            cancellationToken);
        await unit.CommitAsync(cancellationToken);
        logger.LogInformation("Seeded the Registry with the configured workflow {WorkflowId}:{WorkflowVersion}", configured.WorkflowId, configured.WorkflowVersion);
    }

    /// <summary>Inserts the declarations and approved profiles; returns the workflow definition JSON, or null when a concurrent seed won or the configuration is unusable.</summary>
    private async Task<string?> InsertAsync(
        ConfiguredWorkflowOptions configured,
        ICapabilityDeclarationStore declarations,
        IAnalystProfileStore profiles,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var profileVersions = new Dictionary<string, int>(StringComparer.Ordinal);
        var nodeJson = new List<(string NodeKey, JsonObject Node)>();
        foreach (var node in configured.Nodes)
        {
            if (!Sha256Digest.TryParse(node.ImageDigest, out var image)
                || (node.ModelDigest is not null && !Sha256Digest.TryParse(node.ModelDigest, out _)))
            {
                logger.LogWarning("The configured node {NodeKey} has an invalid digest; the Registry is not seeded", node.NodeKey);
                return null;
            }

            var imageDigest = "sha256:" + image.ToString()[Sha256Digest.Prefix.Length..];
            var model = node.ModelDigest ?? RegistryWorkflowDefinitionResolver.NoModelDigest;
            var upstream = configured.Edges
                .Where(e => e.DependentNodeKey == node.NodeKey)
                .Select(e => (Edge: e, Node: configured.Nodes.FirstOrDefault(n => n.NodeKey == e.UpstreamNodeKey)))
                .Where(x => x.Node is not null && x.Node.CapabilityId != node.CapabilityId)
                .GroupBy(x => x.Node!.CapabilityId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            var manifestJson = BuildManifest(node, model, upstream.Select(x => (x.Node!, x.Edge.Kind, x.Edge.Coverage)).ToList());
            var validation = manifestValidator.Validate(imageDigest, manifestJson);
            if (!validation.IsValid || validation.Declaration is null)
            {
                logger.LogWarning(
                    "The seeded manifest of node {NodeKey} is invalid ({Errors}); the Registry is not seeded",
                    node.NodeKey,
                    string.Join(", ", validation.Errors.Select(e => $"{e.Field}:{e.Keyword}")));
                return null;
            }

            var manifestDigest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson)));
            if (!await declarations.TryInsertAsync(
                    new StoredCapabilityDeclaration(validation.Declaration, SeedRepository, manifestDigest, manifestJson, "seed", now, null),
                    cancellationToken))
            {
                return null;
            }

            var content = new AnalystProfileContent(
                node.CapabilityId,
                Semver(node.CapabilityVersion),
                imageDigest,
                model,
                [],
                [],
                node.Resources,
                node.MaxAttempts,
                node.LeaseSeconds,
                node.StaleTimeoutSeconds,
                node.ExecutionTimeoutSeconds,
                VideoPolicies.Optional,
                upstream.ToDictionary(x => x.Node!.CapabilityId, x => Semver(x.Node!.SchemaVersion), StringComparer.Ordinal),
                Semver(node.SchemaVersion));
            var version = profileVersions.GetValueOrDefault(node.AnalystProfileId) + 1;
            profileVersions[node.AnalystProfileId] = version;
            var profile = AnalystProfile.CreateDraft(node.AnalystProfileId, version, content, SeedActor, now).Approve(SeedActor, now);
            if (!await profiles.TryInsertAsync(profile, cancellationToken))
            {
                return null;
            }

            nodeJson.Add((node.NodeKey, new JsonObject
            {
                ["nodeKey"] = node.NodeKey,
                ["profileId"] = profile.ProfileId,
                ["profileVersion"] = profile.Version,
                ["profileDigest"] = profile.Digest.ToString(),
            }));
        }

        var root = new JsonObject
        {
            ["requestedCapabilities"] = new JsonArray(configured.RequestedCapabilities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["completionPolicy"] = configured.CompletionPolicy,
            ["segmentationPolicy"] = new JsonObject
            {
                ["version"] = configured.SegmentationPolicy.Version,
                ["durationSeconds"] = configured.SegmentationPolicy.DurationSeconds,
                ["digest"] = configured.SegmentationPolicy.Digest,
            },
            ["nodes"] = new JsonArray(nodeJson.OrderBy(n => n.NodeKey, StringComparer.Ordinal).Select(n => (JsonNode)n.Node).ToArray()),
            ["edges"] = new JsonArray(configured.Edges
                .OrderBy(e => e.UpstreamNodeKey, StringComparer.Ordinal)
                .ThenBy(e => e.DependentNodeKey, StringComparer.Ordinal)
                .Select(BuildEdge)
                .ToArray()),
        };
        return CanonicalJson.Canonicalize(root.ToJsonString());
    }

    private static JsonNode BuildEdge(ConfiguredEdge e)
    {
        var edge = new JsonObject
        {
            ["upstreamNodeKey"] = e.UpstreamNodeKey,
            ["dependentNodeKey"] = e.DependentNodeKey,
            ["kind"] = e.Kind,
        };
        if (e.Predicate is { } p)
        {
            var predicate = new JsonObject { ["kind"] = p.Kind };
            if (p.CapabilityId is not null)
            {
                predicate["capabilityId"] = p.CapabilityId;
            }

            if (p.NodeKey is not null)
            {
                predicate["nodeKey"] = p.NodeKey;
            }

            edge["predicate"] = predicate;
        }

        return edge;
    }

    private static string BuildManifest(ConfiguredNode node, string model, IReadOnlyList<(ConfiguredNode Node, string Kind, ConfiguredCoverage? Coverage)> upstream)
    {
        var resources = new JsonObject();
        foreach (var pair in node.Resources)
        {
            resources[pair.Key] = pair.Value;
        }

        var upstreamEntries = upstream.Select(u => (Entry: new JsonObject
        {
            ["capabilityId"] = u.Node.CapabilityId,
            ["capabilityVersion"] = Semver(u.Node.CapabilityVersion),
            ["schemaVersion"] = Semver(u.Node.SchemaVersion),
            ["kind"] = u.Kind,
        }, Coverage: PartialCoverage(u.Coverage))).ToList();
        foreach (var (entry, coverage) in upstreamEntries)
        {
            if (coverage is not null)
            {
                entry["coverage"] = coverage;
            }
        }

        var manifest = new JsonObject
        {
            ["manifestVersion"] = upstreamEntries.Any(u => u.Coverage is not null) ? "1.1.0" : "1.0.0",
            ["capability"] = new JsonObject
            {
                ["id"] = node.CapabilityId,
                ["version"] = Semver(node.CapabilityVersion),
                ["tier"] = node.Tier,
                ["executionScope"] = node.ExecutionScope,
            },
            ["upstream"] = new JsonArray(upstreamEntries.Select(u => (JsonNode)u.Entry).ToArray()),
            ["videoAccess"] = VideoPolicies.Optional,
            ["output"] = new JsonObject { ["schemaVersion"] = Semver(node.SchemaVersion) },
            ["compatibleProfiles"] = new JsonArray(JsonValue.Create(node.AnalystProfileId)),
            ["model"] = new JsonObject { ["digest"] = model },
            ["runtime"] = new JsonObject { ["constraints"] = new JsonArray(), ["accelerators"] = new JsonArray() },
            ["resources"] = resources,
            ["requirements"] = new JsonObject
            {
                ["minMaxAttempts"] = 1,
                ["minLeaseSeconds"] = 1,
                ["minStaleTimeoutSeconds"] = 1,
                ["minExecutionTimeoutSeconds"] = 1,
            },
            ["determinism"] = new JsonObject { ["deterministic"] = false, ["idempotent"] = true },
        };
        return manifest.ToJsonString();
    }

    /// <summary>The manifest coverage declaration of a configured partial edge; complete edges declare nothing.</summary>
    private static JsonObject? PartialCoverage(ConfiguredCoverage? coverage)
    {
        if (coverage is null
            || !AnalysisPersistedNames.TryParseCoverageMode(coverage.Mode, out var mode)
            || mode != CoverageMode.Partial)
        {
            return null;
        }

        var declaration = new JsonObject { ["mode"] = "partial" };
        if (coverage.MinimumAcceptedFraction is { } fraction)
        {
            declaration["minimumAcceptedFraction"] = fraction;
        }

        return declaration;
    }

    /// <summary>Manifests and profiles carry semantic versions; the configured development values may be shorter (for example <c>1</c>).</summary>
    private static string Semver(string version) => version.Count(c => c == '.') switch
    {
        0 when ShortVersion().IsMatch(version) => version + ".0.0",
        1 when ShortVersion().IsMatch(version) => version + ".0",
        _ => version,
    };

    [GeneratedRegex(@"^\d+(\.\d+)?$")]
    private static partial Regex ShortVersion();
}

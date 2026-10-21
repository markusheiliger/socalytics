using System.Text.Json;
using SocAlytics.Platform.Domain.AgentOrchestration;
using SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

namespace SocAlytics.Platform.Application.AgentOrchestration.Catalog;

/// <summary>
/// The normalized Manhattan distance of a philosophy profile to the profile of one approved, non-retired Coach Agent
/// version. <see cref="Distance"/> is rounded to two decimals for display; <see cref="ReviewRequired"/> uses the unrounded value.
/// </summary>
public sealed record ProfileDistanceView(
    string AgentId, string AgentVersion, string ProfileId, string ProfileVersion, double Distance, bool ReviewRequired);

/// <summary>Computes profile distances inside the caller's unit of work.</summary>
public sealed class ProfileDistanceCalculator(IAgentVersionStore versions, IAgentCatalogArtifactStore artifacts)
{
    /// <summary>
    /// Compares the profile with the profiles of approved, non-retired Coach Agent versions over the same dimension-set
    /// identifier and major version. The identical profile record and, when given, the excluded agent version are skipped.
    /// </summary>
    public async Task<IReadOnlyList<ProfileDistanceView>> ComputeAsync(
        string profileId, string profileVersion, string profileDocument, string? excludeAgentId, string? excludeAgentVersion, CancellationToken cancellationToken)
    {
        var subject = Read(profileDocument);
        if (subject is null)
        {
            return [];
        }

        var result = new List<ProfileDistanceView>();
        var coaches = await versions.ListAsync(null, AgentVersionState.Approved, cancellationToken);
        foreach (var coach in coaches.Where(c => string.Equals(c.Group, AgentCatalogIds.GroupCoach, StringComparison.Ordinal)))
        {
            if (string.Equals(coach.AgentId, excludeAgentId, StringComparison.Ordinal)
                && string.Equals(coach.AgentVersion, excludeAgentVersion, StringComparison.Ordinal))
            {
                continue;
            }

            var reference = ProfileReferenceOf(coach.Document);
            if (reference is not { } other
                || (string.Equals(other.Id, profileId, StringComparison.Ordinal) && string.Equals(other.Version, profileVersion, StringComparison.Ordinal)))
            {
                continue;
            }

            var stored = await artifacts.FindAsync(AgentCatalogRecordKind.PhilosophyProfile, other.Id, other.Version, cancellationToken);
            var candidate = stored is null ? null : Read(stored.Document);
            if (candidate is null
                || !string.Equals(candidate.DimensionSetId, subject.DimensionSetId, StringComparison.Ordinal)
                || candidate.DimensionSetMajor != subject.DimensionSetMajor)
            {
                continue;
            }

            var distance = ProfileDistance.Between(subject.Weights, candidate.Weights);
            result.Add(new(coach.AgentId, coach.AgentVersion, other.Id, other.Version, Math.Round(distance, 2), ProfileDistance.RequiresReview(distance)));
        }

        return [.. result.OrderBy(d => d.AgentId, StringComparer.Ordinal).ThenBy(d => d.AgentVersion, StringComparer.Ordinal)];
    }

    /// <summary>The distances of a Coach Agent version's own profile to the other approved Coach Agent versions.</summary>
    public async Task<IReadOnlyList<ProfileDistanceView>> ComputeForAgentVersionAsync(AgentVersionRecord record, CancellationToken cancellationToken)
    {
        if (!string.Equals(record.Group, AgentCatalogIds.GroupCoach, StringComparison.Ordinal) || ProfileReferenceOf(record.Document) is not { } reference)
        {
            return [];
        }

        var profile = await artifacts.FindAsync(AgentCatalogRecordKind.PhilosophyProfile, reference.Id, reference.Version, cancellationToken);
        return profile is null
            ? []
            : await ComputeAsync(profile.Id, profile.Version, profile.Document, record.AgentId, record.AgentVersion, cancellationToken);
    }

    private static (string Id, string Version)? ProfileReferenceOf(string agentVersionDocument)
    {
        using var document = JsonDocument.Parse(agentVersionDocument);
        if (!document.RootElement.TryGetProperty("artifacts", out var artifactsElement)
            || !artifactsElement.TryGetProperty("philosophyProfile", out var profile)
            || !profile.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
            || !profile.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return (id.GetString()!, version.GetString()!);
    }

    private static ProfileSubject? Read(string profileDocument)
    {
        using var document = JsonDocument.Parse(profileDocument);
        var root = document.RootElement;
        if (!root.TryGetProperty("dimensionSet", out var set)
            || !set.TryGetProperty("id", out var setId) || setId.ValueKind != JsonValueKind.String
            || !set.TryGetProperty("version", out var setVersion) || setVersion.ValueKind != JsonValueKind.String
            || SemanticVersion.MajorOf(setVersion.GetString()) is not { } major
            || !root.TryGetProperty("weights", out var weightsElement) || weightsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in weightsElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("weight", out var weight) && weight.TryGetInt32(out var value))
            {
                weights[property.Name] = value;
            }
        }

        return new(setId.GetString()!, major, weights);
    }

    private sealed record ProfileSubject(string DimensionSetId, int DimensionSetMajor, IReadOnlyDictionary<string, int> Weights);
}

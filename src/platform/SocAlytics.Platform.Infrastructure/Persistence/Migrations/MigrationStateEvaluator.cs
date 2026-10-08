using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;

namespace SocAlytics.Platform.Infrastructure.Persistence.Migrations;

internal static class MigrationStateEvaluator
{
    public static MigrationState Evaluate(MigrationCatalog catalog, IReadOnlyCollection<MigrationHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(history);

        var applied = history.ToDictionary(e => e.Identity, StringComparer.Ordinal);
        var catalogIdentities = catalog.Scripts.Select(s => s.Identity).ToHashSet(StringComparer.Ordinal);
        var unknown = history
            .Where(e => !catalogIdentities.Contains(e.Identity))
            .OrderBy(e => e.Sequence)
            .Select(e => e.Identity)
            .ToList();

        foreach (var script in catalog.Scripts)
        {
            if (applied.TryGetValue(script.Identity, out var entry)
                && !string.Equals(entry.Checksum, script.Checksum, StringComparison.Ordinal))
            {
                return MigrationState.ChecksumMismatch(script.Identity, unknown);
            }
        }

        var pending = catalog.Scripts.Where(s => !applied.ContainsKey(s.Identity)).ToList();

        if (history.Count > 0)
        {
            var highestApplied = history.Max(e => e.Sequence);
            var conflict = pending.FirstOrDefault(s => s.Sequence <= highestApplied);
            if (conflict is not null)
            {
                return MigrationState.SequenceConflict(conflict.Identity, unknown);
            }
        }

        return pending.Count > 0
            ? MigrationState.Pending(pending, unknown)
            : MigrationState.Current(unknown);
    }
}

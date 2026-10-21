namespace SocAlytics.Platform.Domain.Analysis;

// Version key of a run: finalization time first, the version id as tie-break.
public readonly record struct RecordingSetVersionKey(DateTimeOffset FinalizedAt, Guid VersionId)
	: IComparable<RecordingSetVersionKey>
{
	public int CompareTo(RecordingSetVersionKey other)
	{
		var byTime = FinalizedAt.UtcDateTime.CompareTo(other.FinalizedAt.UtcDateTime);
		return byTime != 0 ? byTime : RecordingSetVersionOrder.CompareIds(VersionId, other.VersionId);
	}
}

// Position of a run in the run order: (version key, created_at, id), newest first when descending.
public readonly record struct AnalysisRunOrderKey(RecordingSetVersionKey Version, DateTimeOffset CreatedAt, Guid RunId)
	: IComparable<AnalysisRunOrderKey>
{
	public int CompareTo(AnalysisRunOrderKey other)
	{
		var byVersion = Version.CompareTo(other.Version);
		if (byVersion != 0)
		{
			return byVersion;
		}

		var byCreated = CreatedAt.UtcDateTime.CompareTo(other.CreatedAt.UtcDateTime);
		return byCreated != 0 ? byCreated : RecordingSetVersionOrder.CompareIds(RunId, other.RunId);
	}
}

public static class RecordingSetVersionOrder
{
	// A version is only older when its key is strictly smaller; the same version is never older.
	public static bool IsOlder(RecordingSetVersionKey candidate, RecordingSetVersionKey than) =>
		candidate.CompareTo(than) < 0;

	// Newest first: the run order descending.
	public static IComparer<AnalysisRunOrderKey> NewestFirst { get; } =
		Comparer<AnalysisRunOrderKey>.Create(static (left, right) => right.CompareTo(left));

	// The current run is the first run in the run order; none for no runs.
	public static T? SelectCurrent<T>(IEnumerable<T> runs, Func<T, AnalysisRunOrderKey> key)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(runs);
		ArgumentNullException.ThrowIfNull(key);

		T? current = null;
		AnalysisRunOrderKey currentKey = default;
		foreach (var run in runs)
		{
			var runKey = key(run);
			if (current is null || runKey.CompareTo(currentKey) > 0)
			{
				current = run;
				currentKey = runKey;
			}
		}

		return current;
	}

	// Runs whose version is older than the current run's version, in id order.
	public static IReadOnlyList<T> SelectOlderThanCurrent<T>(IEnumerable<T> runs, Func<T, AnalysisRunOrderKey> key, Func<T, Guid> id)
		where T : class
	{
		ArgumentNullException.ThrowIfNull(runs);
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(id);

		var all = runs as IReadOnlyCollection<T> ?? runs.ToList();
		var current = SelectCurrent(all, key);
		if (current is null)
		{
			return [];
		}

		var currentVersion = key(current).Version;
		return all
			.Where(run => IsOlder(key(run).Version, currentVersion))
			.OrderBy(id, Comparer<Guid>.Create(CompareIds))
			.ToList();
	}

	// Matches the uuid ordering of PostgreSQL (bytewise, big-endian), not Guid.CompareTo.
	internal static int CompareIds(Guid left, Guid right)
	{
		Span<byte> leftBytes = stackalloc byte[16];
		Span<byte> rightBytes = stackalloc byte[16];
		left.TryWriteBytes(leftBytes, bigEndian: true, out _);
		right.TryWriteBytes(rightBytes, bigEndian: true, out _);
		return leftBytes.SequenceCompareTo(rightBytes);
	}
}

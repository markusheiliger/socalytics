namespace SocAlytics.Platform.Domain.Analysis;

public readonly record struct FactRange
{
	public FactRange(long startMs, long endMs)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(startMs);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endMs, startMs);
		StartMs = startMs;
		EndMs = endMs;
	}

	public long StartMs { get; }

	public long EndMs { get; }

	public static bool TryCreate(long startMs, long endMs, out FactRange range)
	{
		if (startMs < 0 || endMs <= startMs)
		{
			range = default;
			return false;
		}

		range = new FactRange(startMs, endMs);
		return true;
	}

	public bool IsWithin(FactRange window) => StartMs >= window.StartMs && EndMs <= window.EndMs;

	// Either bound may be omitted (open); both ranges are half-open, so shared boundaries do not overlap.
	public bool Overlaps(long? fromMs, long? toMs) =>
		(toMs is null || StartMs < toMs) && (fromMs is null || EndMs > fromMs);
}

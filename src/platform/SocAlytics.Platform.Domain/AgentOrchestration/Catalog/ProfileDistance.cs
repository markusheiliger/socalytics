namespace SocAlytics.Platform.Domain.AgentOrchestration.Catalog;

public static class ProfileDistance
{
	// Approving a Coach Agent version closer than this to another approved profile needs an acknowledged review.
	public const double ReviewThreshold = 0.10;

	// sum(|a_i - b_i|) / 200 over the union of dimension ids; a missing dimension weighs 0.
	public static double Between(IReadOnlyDictionary<string, int> first, IReadOnlyDictionary<string, int> second)
	{
		ArgumentNullException.ThrowIfNull(first);
		ArgumentNullException.ThrowIfNull(second);

		var sum = 0;
		foreach (var id in first.Keys.Union(second.Keys, StringComparer.Ordinal))
		{
			first.TryGetValue(id, out var a);
			second.TryGetValue(id, out var b);
			sum += Math.Abs(a - b);
		}

		return sum / 200.0;
	}

	public static bool RequiresReview(double distance) => distance < ReviewThreshold;
}

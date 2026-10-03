using Xunit;

// PostgreSQL roles are cluster-wide, so concurrent bootstraps in one container would race.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

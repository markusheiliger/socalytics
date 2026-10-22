namespace SocAlytics.Platform.Domain.Segments;

public static class SegmentFailureReasons
{
	public const string CoverageEmpty = "segment-coverage-empty";
	public const string CoverageIncomplete = "segment-coverage-incomplete";
	public const string PolicyUnknown = "segmentation-policy-unknown";
	public const string PolicyConflict = "segmentation-policy-conflict";
	public const string ScopeMismatch = "segment-scope-mismatch";
	public const string SourceUnavailable = "source-unavailable";
	public const string ToolchainUnavailable = "toolchain-unavailable";
	public const string ExtractionFailed = "extraction-failed";
	public const string StorageUnavailable = "storage-unavailable";
	public const string TooLarge = "segment-too-large";
	public const string MediaInvalid = "segment-media-invalid";
	public const string RetryBudgetExhausted = "retry-budget-exhausted";

	public static IReadOnlyList<string> All { get; } =
	[
		CoverageEmpty,
		CoverageIncomplete,
		PolicyUnknown,
		PolicyConflict,
		ScopeMismatch,
		SourceUnavailable,
		ToolchainUnavailable,
		ExtractionFailed,
		StorageUnavailable,
		TooLarge,
		MediaInvalid,
		RetryBudgetExhausted,
	];

	public static bool IsKnown(string? reason) => reason is not null && All.Contains(reason);
}

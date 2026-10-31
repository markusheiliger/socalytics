namespace SocAlytics.Platform.Domain.AgentOrchestration.Tools;

public enum ToolCallOutcome
{
	Answered,
	Unavailable,
	NotFound,
	NotPermitted,
	Invalid,
	LimitReached,
	ScopeLost,
	Failed,
}

public enum ToolCallReason
{
	CapabilityNotInRun,
	NoAcceptedProducer,
	EvidenceStorageUnavailable,
	Interrupted,
}

// A bounded reference to an accepted fact; never fact content.
public sealed record ToolCallFactEvidence(Guid AcceptedResultId, int Ordinal);

// A bounded reference to an available media part; never a link.
public sealed record ToolCallMediaEvidence(string MediaDigest, long StartMs, long EndMs);

public static class ToolCallNames
{
	public static string ToWireName(ToolCallOutcome outcome) => outcome switch
	{
		ToolCallOutcome.Answered => "answered",
		ToolCallOutcome.Unavailable => "unavailable",
		ToolCallOutcome.NotFound => "not-found",
		ToolCallOutcome.NotPermitted => "not-permitted",
		ToolCallOutcome.Invalid => "invalid",
		ToolCallOutcome.LimitReached => "limit-reached",
		ToolCallOutcome.ScopeLost => "scope-lost",
		ToolCallOutcome.Failed => "failed",
		_ => throw new ArgumentOutOfRangeException(nameof(outcome)),
	};

	public static bool TryParseOutcome(string? value, out ToolCallOutcome outcome)
	{
		switch (value)
		{
			case "answered": outcome = ToolCallOutcome.Answered; return true;
			case "unavailable": outcome = ToolCallOutcome.Unavailable; return true;
			case "not-found": outcome = ToolCallOutcome.NotFound; return true;
			case "not-permitted": outcome = ToolCallOutcome.NotPermitted; return true;
			case "invalid": outcome = ToolCallOutcome.Invalid; return true;
			case "limit-reached": outcome = ToolCallOutcome.LimitReached; return true;
			case "scope-lost": outcome = ToolCallOutcome.ScopeLost; return true;
			case "failed": outcome = ToolCallOutcome.Failed; return true;
			default: outcome = default; return false;
		}
	}

	public static string ToWireName(ToolCallReason reason) => reason switch
	{
		ToolCallReason.CapabilityNotInRun => "capability-not-in-run",
		ToolCallReason.NoAcceptedProducer => "no-accepted-producer",
		ToolCallReason.EvidenceStorageUnavailable => "evidence-storage-unavailable",
		ToolCallReason.Interrupted => "interrupted",
		_ => throw new ArgumentOutOfRangeException(nameof(reason)),
	};

	public static bool TryParseReason(string? value, out ToolCallReason reason)
	{
		switch (value)
		{
			case "capability-not-in-run": reason = ToolCallReason.CapabilityNotInRun; return true;
			case "no-accepted-producer": reason = ToolCallReason.NoAcceptedProducer; return true;
			case "evidence-storage-unavailable": reason = ToolCallReason.EvidenceStorageUnavailable; return true;
			case "interrupted": reason = ToolCallReason.Interrupted; return true;
			default: reason = default; return false;
		}
	}
}

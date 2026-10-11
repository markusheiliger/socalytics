namespace SocAlytics.Platform.Domain.Analysis;

public enum ExecutionAttemptState
{
	Active,
	Stale,
	Failed,
	Accepted,
	Rejected,
}

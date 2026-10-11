namespace SocAlytics.Platform.Domain.Analysis;

public enum AnalysisRunState
{
	Requested,
	Running,
	PartiallyCompleted,
	Completed,
	Failed,
	Cancelled,
}

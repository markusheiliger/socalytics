namespace SocAlytics.Platform.Domain.Analysis;

public enum LogicalJobState
{
	Waiting,
	Ready,
	Claimed,
	Completed,
	Failed,
	Blocked,
}

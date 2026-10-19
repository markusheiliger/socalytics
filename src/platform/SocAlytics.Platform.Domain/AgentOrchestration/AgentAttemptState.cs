namespace SocAlytics.Platform.Domain.AgentOrchestration;

public enum AgentAttemptState
{
	Claimed,
	Active,
	Completed,
	Failed,
	Expired,
	Fenced,
}

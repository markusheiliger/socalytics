namespace SocAlytics.Platform.Domain.AgentOrchestration;

public enum AgentStaleDisposition
{
	AttemptNotCurrent,
	LeaseExpired,
	AttemptFenced,
	InvocationEnded,
	ScopeLost,
}

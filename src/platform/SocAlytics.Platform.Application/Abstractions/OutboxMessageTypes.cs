namespace SocAlytics.Platform.Application.Abstractions;

public static class OutboxMessageTypes
{
    public const string RecordingsFinalized = "matches.recordings-finalized";
    public const string AnalysisReadyWork = "analysis.ready-work";
    public const string AnalysisRunStateChanged = "analysis.run-state-changed";
    public const string AgentConversationPurge = "agent-conversation-purge";
}

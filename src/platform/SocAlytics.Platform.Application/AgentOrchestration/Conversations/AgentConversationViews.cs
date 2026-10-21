namespace SocAlytics.Platform.Application.AgentOrchestration.Conversations;

/// <summary>A conversation with whether its run is still the match's current run.</summary>
public sealed record AgentConversationView(AgentConversationRecord Conversation, bool AnalysisRunIsCurrent);

public sealed record AvailableAgentView(string AgentId, string Group, string Domain, string DisplayName, string EnabledVersion);

public sealed record AgentConversationList(IReadOnlyList<AgentConversationView> Items);

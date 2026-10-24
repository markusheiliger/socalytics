import type { ConversationStateFilter } from '../../platform/models/agents';

export const triageKeys = {
  agents: () => ['triage', 'agents'] as const,
  conversations: (matchId: string, state?: ConversationStateFilter) =>
    (state ? (['triage', 'conversations', matchId, state] as const) : (['triage', 'conversations', matchId] as const)),
  conversation: (conversationId: string) => ['triage', 'conversation', conversationId] as const,
  turns: (conversationId: string) => ['triage', 'turns', conversationId] as const,
  invocation: (invocationId: string) => ['triage', 'invocation', invocationId] as const,
  lineage: (invocationId: string) => ['triage', 'lineage', invocationId] as const,
};

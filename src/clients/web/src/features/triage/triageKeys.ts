export const triageKeys = {
  agents: () => ['triage', 'agents'] as const,
  conversations: (matchId: string) => ['triage', 'conversations', matchId] as const,
  conversation: (conversationId: string) => ['triage', 'conversation', conversationId] as const,
  turns: (conversationId: string) => ['triage', 'turns', conversationId] as const,
  invocation: (invocationId: string) => ['triage', 'invocation', invocationId] as const,
  lineage: (invocationId: string) => ['triage', 'lineage', invocationId] as const,
};

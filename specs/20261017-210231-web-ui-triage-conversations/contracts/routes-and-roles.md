# Contract: Routes and Roles (additions)

Extends the Web UI's route contracts of
[Web UI Foundation](../../archive/20261012-150657-web-ui-foundation/contracts/routes-and-roles.md)
and
[Web UI Match Workflow and Recording Upload](../../archive/20261013-112844-web-ui-match-upload/contracts/routes-and-roles.md).
"Reader" means a Club Admin, or a Coach or Viewer of the match's team; "Editor" means a Club Admin or a Coach of the
match's team (`canEditTeamMatches`). The platform stays authoritative: the owner-only reads of
[Agent Orchestration Direct Invocations](../../archive/20261017-204332-agent-orchestration-invocations/spec.md) decide what a
member sees.

| Route | View | Allowed for | Platform operations |
| --- | --- | --- | --- |
| `/matches/:matchId` | Match view, gaining the triage section after the run list: the member's own conversations for the match (newest first, each with bound run, participants, creation time, and a "no longer current run" marker) and, for Editors with an eligible run, "Start triage conversation" | Reader (start action: Editor) | as before, plus `listAgentConversations`, and `createAgentConversation` from the start dialog (runs from the existing `listMatchAnalysisRuns`) |
| `/matches/:matchId/triage/:conversationId` | Triage conversation: bound run with link to `/matches/:matchId/analysis-runs/:runId`, superseded-run notice, participants with add control, history with "Show more", pending turns, advice, lineage panels, question composer | Reader who owns the conversation (controls: Editor) | `getAgentConversation`, `listAvailableAgents`, `addAgentConversationParticipant`, `listAgentConversationTurns`, `submitAgentInvocation`, `getAgentInvocation`, `getAgentInvocationLineage` |

## Rules

- The conversation view loads the match through the match context and shows the not-permitted view when the member has
  no role on the match's team, as the match view does.
- A conversation that the platform does not return (`404`), or whose `matchId` differs from `:matchId`, shows the
  not-found view without any conversation detail, and stops all polling.
- Editors see the start action, the add-participant control, and the composer; Viewers see none of them and a
  read-only note. A `403` on submission switches the view to read-only.
- The start action appears only when the run list contains a `Completed` or `PartiallyCompleted` run; otherwise the
  section says that a finished analysis is needed first. Its dialog preselects the newest eligible run.
- No navigation item is added; the conversation is reached from the match view, and its back link returns to the match.
- The run review view of [Web UI Connected Analysis Review](../../archive/20261017-181118-web-ui-analysis-review/spec.md) is
  only linked to; it gains no triage control.
- Leaving the conversation view with an unsent question or an unknown-outcome submission asks for confirmation
  (browser unload and in-app navigation).
- Without a network both views show the connection-required state and offer no state-changing action.

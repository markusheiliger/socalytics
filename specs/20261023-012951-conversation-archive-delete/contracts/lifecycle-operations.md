# Contract: Conversation Lifecycle Operations

All operations use `MapMemberApi()` (policy `ActiveMember`, session anti-forgery), `IfMatchHeader`, and `ProblemResults`.
Responses carry the conversation's `ETag` (its version).

| Operation id | Request | Success | Problems |
| --- | --- | --- | --- |
| `archiveAgentConversation` | `POST /api/v1/agent-conversations/{conversationId}/archive`, `If-Match` | `200` `AgentConversationDto` with `state: "archived"` | `401`, `403` Viewer, `404`, `412` stale version, `428` missing version |
| `unarchiveAgentConversation` | `POST /api/v1/agent-conversations/{conversationId}/unarchive`, `If-Match` | `200` `AgentConversationDto` with `state: "active"` | as above |
| `deleteAgentConversation` | `DELETE /api/v1/agent-conversations/{conversationId}`, `If-Match` | `204` no content | `401`, `403` Viewer, `404`, `412`, `428` |

Changed existing operations:

- `listAgentConversations`: query `state` (`active` default, `archived`); every item and `getAgentConversation` expose `state` (`active` or `archived`; `deleted` is never returned).
- `submitAgentInvocation` and `addAgentConversationParticipant`: `409` with reason `conversation-archived` while archived.
- All conversation, turn, invocation, and lineage reads and workload operations: `404` for a deleted conversation.

Semantics:

- A request already satisfied by the current state succeeds without change, new version, or audit event, whatever `If-Match` carries; otherwise a stale `If-Match` is `412` with the current version.
- Repeat `DELETE` by the initiating member still in the owning team's Coach or Club Admin scope returns `204` without a second tombstone; every other caller gets `404`.
- Problem bodies and audit records never contain question, advice, evidence, or lineage text.

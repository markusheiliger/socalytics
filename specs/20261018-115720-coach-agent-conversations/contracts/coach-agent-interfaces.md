# Contract: Coach Agent Interfaces

Changes to the interfaces of [Agent Orchestration API](../../archive/20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md),
[Agent Catalog API](../../20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md), the
[Hermes Runtime Interface](../../20261018-113729-hermes-runtime/contracts/hermes-runtime.md), and the Web UI adapters of
[Web UI Match Triage Conversations](../../20261017-210231-web-ui-triage-conversations/contracts/agent-conversation-adapter.md)
and [Web UI Agent Catalog Administration](../../20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-adapter.md).
Everything not listed here is unchanged. The OpenAPI document and its committed snapshot are refreshed.

## Machine-readable contracts

| Contract | New release | Change |
| --- | --- | --- |
| `contracts/agent-orchestration/work-notification/v1` | 1.1.0 | `route` enum: `user-to-specialist`, `user-to-coach` |
| `contracts/agent-orchestration/execution-envelope/v1` | 1.2.0 | `route` enum: `user-to-specialist`, `user-to-coach`; `agent.artifacts[].kind` includes `dimension-set` |
| `contracts/agent-orchestration/attempt-report/v1` | 1.2.0 | [attempt-report-1.2.0.md](attempt-report-1.2.0.md) |

Each keeps its earlier releases under `releases/` and gains valid fixtures for `user-to-coach` (a notification, an
envelope with eight pins) and an invalid fixture with `route: "coach-to-coach"`. `contracts/README.md` index rows
name the new versions.

## Member operations

| Operation | Change |
| --- | --- |
| `setAgentEnablement` | Approved Coach Agent versions are enabled like Specialist Agent versions; `409 coach-agent-enablement-unsupported` is removed. |
| `listAvailableAgents` | Lists enabled agents of both groups; each item adds `disclosure` (string; null for Specialist Agents). |
| `addAgentConversationParticipant` | Accepts enabled Coach Agents. |
| `submitAgentInvocation` | Unchanged request; the invocation's route follows the agent's group; Coach Agents pin the dimension set too. |

### Member reads

| Operation | Added |
| --- | --- |
| `listAgentConversationTurns` | per turn: `disclosure`, `appliedPhilosophyProfile` (object or null) |
| `getAgentInvocation` | `route` may be `user-to-coach`; `disclosure`; `appliedPhilosophyProfile`; `agent.artifacts` lists the `dimension-set` pin |
| `getAgentInvocationLineage` | `disclosure`; `appliedPhilosophyProfile` on the advice; checkpoint entries of kind `profile-rendered` (`details.appliedPhilosophyProfile`) and `modifiers-omitted` (`details.note`) |

`appliedPhilosophyProfile` has the shape of the contract's definition. Authorization is unchanged (owner only).

## Workload operations

| Operation | Change |
| --- | --- |
| `claimAgentInvocation` | Returns `execution-envelope` 1.2.0. |
| `readPinnedAgentArtifacts` | Coach Agent attempts return the pinned dimension set as one of `artifacts` (kind `dimension-set`). |
| `recordAgentCheckpoint`, `completeAgentAttempt` | Accept `attempt-report` 1.2.0 and apply the checks of [attempt-report-1.2.0.md](attempt-report-1.2.0.md). |
| Agent tool operations | Unchanged; apply to `user-to-coach` attempts exactly as to `user-to-specialist` attempts. |

## Hermes

| Item | Change |
| --- | --- |
| Routes | `user-to-specialist` and `user-to-coach`; anything else fails `unsupported-route` (not retryable). |
| Copied releases | Adds `dimension-set`, `philosophy-profile`, `modifier-policy` 1.0.0, `work-notification` 1.1.0, `execution-envelope` 1.2.0, `attempt-report` 1.2.0. |
| Rendering | `{{philosophy_profile}}` → `hermes-profile-block/1`; runtime section followed by `hermes-coach-section/1` ([research.md R5](../research.md#r5---profile-block-rendering-hermes-profile-block1)). |
| Local function tool | `propose_context_modifiers` (Coach Agent attempts only; [research.md R6](../research.md#r6---context-modifier-proposal-and-validation)). |
| Checkpoints | Adds `profile-rendered` and `modifiers-omitted`. |
| Declared runtime | `hermes/<version>+hermes-runtime-section/1+hermes-coach-section/1+hermes-profile-block/1` for Coach Agents. |
| Logs | Adds the fields `route` and `modifierOutcome` (`applied`, `omitted`, `not-proposed`) and the rule code as `reasonCode`; never weights, reasons, prompt, or rendered text. |
| Script (scripted provider) | Unchanged schema; scenarios use `toolCalls` with `toolId: "propose_context_modifiers"`; `{{callIds}}` may also appear in tool-call arguments. |

## Web UI adapters

- `agentConversationAdapter`: maps `group`, `disclosure`, and `appliedPhilosophyProfile` (with derived per-dimension
  rows) and the new lineage checkpoint kinds; unknown values stay raw.
- `agentCatalogAdapter`: no longer maps `coach-agent-enablement-unsupported` to a dedicated outcome (an unexpected
  occurrence is shown as an unknown refusal with its raw code).

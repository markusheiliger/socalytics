# Data Model: Coach Agent Direct Conversations

## Persisted changes (platform)

One forward-only migration `NNNN_agentorchestration_coach_agents.sql` in
`src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, numbered at implementation time as the next
free number on an up-to-date default branch:

| Table | Change |
| --- | --- |
| `agent_invocations` | `route` check constraint allows `user-to-specialist` and `user-to-coach`. |
| `agent_accepted_advice` | New nullable `applied_philosophy_profile jsonb`; set only for `user-to-coach` advice. The table keeps its classification. |
| `agent_invocation_pins` | No schema change; Coach Agent invocations add a `dimension-set` row. |
| `agent_lineage_entries` | No schema change; `checkpoint` payloads of kind `profile-rendered` carry `appliedPhilosophyProfile`, and `completed` payloads carry it for Coach Agent completions. |

No new table, so `PersistedTableClassifications` gains no entry.

## Invocation route

| Agent group (catalog baseline) | Route | Executed by |
| --- | --- | --- |
| `specialist` | `user-to-specialist` | Hermes (`user-to-specialist` rules) or the simulated agent workload |
| `coach` | `user-to-coach` | Hermes (Coach Agent rules) or the simulated agent workload |

## Pins of a Coach Agent invocation

`agent-version` plus `prompt-template`, `model-selection`, `tool-policy`, `safety-policy`, `philosophy-profile`,
`modifier-policy` (from the agent version), and `dimension-set` (from the profile), each `{ kind, id, version,
digest }`, inserted with the invocation and never changed.

## Applied philosophy profile (`attempt-report` 1.2.0)

| Field | Rules |
| --- | --- |
| `renderingVersion` | `hermes-profile-block/<n>` or `simulated-profile/<n>`; pattern `^[a-z][a-z0-9-]{0,62}/[0-9]{1,4}$`. |
| `dimensionSet`, `profile`, `modifierPolicy` | `{ id, version, digest }`; equal to the pins. |
| `baseWeights` | Object keyed by dimension id, integer 0–100; equal to the pinned profile's weights. |
| `modifiers` | 0–5 items `{ category, deltas, reason, evidenceCallIds }`: `category` one of the pinned policy's categories, at most one item per category; `deltas` object of 1–8 dimension ids with non-zero integers; `reason` 1–300 characters; `evidenceCallIds` 1–10 unique call identifiers. |
| `appliedWeights` | Object with the same keys as `baseWeights`; `appliedWeights[d] = baseWeights[d] + Σ deltas[d]`. |

The before and after values of every modifier are the base and applied weights of the dimensions its `deltas` name;
the policy version is the `modifierPolicy` reference.

Rules checked by `AppliedPhilosophyProfileRules` (Domain) and by Hermes before rendering (R6):

- per dimension `|Σ deltas[d]| ≤ perDimensionBound`; `Σ_d Σ deltas[d] = 0`; `Σ_d max(0, Σ deltas[d]) ≤ maxPositiveMovement`;
- `appliedWeights[d] ≥ 0` and `Σ appliedWeights = 100`;
- every `evidenceCallIds` entry is an `agent_tool_calls` row of the same attempt with outcome `answered`;
- the object is present only for `user-to-coach` attempts, required on their completions, and the completion's object
  equals the attempt's last `profile-rendered` checkpoint.

Violation codes (problem `violations[].code`): `applied-profile-missing`, `applied-profile-not-allowed`,
`reference-mismatch`, `base-weights-mismatch`, `category-not-permitted`, `duplicate-category`, `unknown-dimension`,
`delta-out-of-bounds`, `not-budget-neutral`, `positive-movement-exceeded`, `negative-weight`, `applied-weights-mismatch`,
`evidence-call-unknown`, `evidence-not-answered`, `last-checkpoint-mismatch`.

## Checkpoints (Coach Agent attempts, in addition to Hermes' existing kinds)

| Kind | Content |
| --- | --- |
| `profile-rendered` | `note`: `block <renderingVersion>; modifiers <n>`; `appliedPhilosophyProfile` required. |
| `modifiers-omitted` | `note`: one rule code of [research.md R6](research.md#r6---context-modifier-proposal-and-validation); no snapshot. |

## Hermes attempt execution (memory only)

The attempt execution of the [Hermes Agent Runtime data model](../20261018-113729-hermes-runtime/data-model.md#attempt-execution-memory-one-per-claimed-attempt)
gains, for `user-to-coach`:

| Field | Rules |
| --- | --- |
| `philosophy` | Pinned dimension set, profile, and modifier policy, validated (R4). |
| `toolResults` | `callId` → platform `outcome` of every MCP tool result passed to the model in this attempt. |
| `modifierState` | `not-evaluated`, `applied` (with the set), or `omitted` (with the rule code); evaluated once. |
| `appliedProfile` | Current snapshot; rendered into the system message and recorded as `profile-rendered`. |

Lifecycle additions:

```text
claimed --route user-to-coach, artifacts valid--> resolved --profile-rendered checkpoint--> running
running --first propose_context_modifiers, valid--> running (profile-rendered checkpoint, system message re-rendered)
running --first propose_context_modifiers, invalid--> running (modifiers-omitted checkpoint)
running --later propose_context_modifiers--> running (modifiers-omitted: already-evaluated)
running --profile-rendered checkpoint 400 applied-philosophy-profile-invalid--> failing(runtime-error)
completing --409 applied-philosophy-profile-invalid--> failing(runtime-error)
```

Failure reasons are unchanged; Coach Agent artifact violations use `invalid-artifact` and `incompatible-artifact`,
other routes `unsupported-route`.

## Member read additions

| Read | Added fields |
| --- | --- |
| `listAvailableAgents` | `disclosure` (string or null). |
| `listAgentConversationTurns` (each turn) | `disclosure`, `appliedPhilosophyProfile` (null until succeeded or for Specialist Agents). |
| `getAgentInvocation` | `route` value `user-to-coach`; `disclosure`; `appliedPhilosophyProfile`; `agent.artifacts` includes `dimension-set`. |
| `getAgentInvocationLineage` | `disclosure`; `appliedPhilosophyProfile` on the advice; `profile-rendered` and `modifiers-omitted` checkpoint entries with their details. |

## Web UI client models (`src/clients/web/src/platform/models/agents.ts`)

- `AvailableAgent`: adds `group: 'coach' | 'specialist' | { raw: string }` (unknown values kept raw) and
  `disclosure: string | null`.
- `Turn`, `Invocation`, `Lineage`: add `disclosure: string | null` and
  `appliedPhilosophyProfile: AppliedPhilosophyProfile | null`, where `AppliedPhilosophyProfile` mirrors the contract and
  adds a derived per-dimension row list `{ dimensionId, base, modifier, applied }` in the order of `baseWeights`.
- Lineage entry details: `profile-rendered` (snapshot) and `modifiers-omitted` (rule code) are known checkpoint kinds;
  others stay raw.

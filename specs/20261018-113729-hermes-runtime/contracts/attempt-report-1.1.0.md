# Contract: Attempt Report 1.1.0 (completion execution references)

Change to `contracts/agent-orchestration/attempt-report/v1` of
[Agent Orchestration Direct Invocations](../../20261017-204332-agent-orchestration-invocations/contracts/agent-orchestration-api.md#machine-readable-contracts-contractsagent-orchestration).
Release `1.0.0` stays unchanged; `releases/1.1.0.schema.json` is added and the main schema becomes 1.1.0.

## Schema change

`completion.executionReferences` gains two optional properties; all existing properties and rules are unchanged, and
additional properties stay refused:

| Property | Type | Rules |
| --- | --- | --- |
| `modelSelectionRef` | string | `^sha-256:[0-9a-f]{64}$`; the pinned model selection digest |
| `parameters` | object | Closed; optional `temperature` (0–2), `topP` (0–1), `maxOutputTokens` (integer 1–32,768), `reasoningEffort` (`low`, `medium`, `high`), the bounds of `contracts/agent-catalog/model-selection` |

Fixtures: valid `completion-1.1.0.json` (both properties), valid `completion-1.0.0-shape.json` (neither, still valid);
invalid `completion-parameters-extra.json` (`parameters.apiKey`), `completion-model-selection-ref-malformed.json`.

## Platform behavior (`completeAgentAttempt`)

In addition to the existing checks of `promptRef`, `toolPolicyRef`, and `safetyPolicyRef`, the completion is refused
with `409 execution-references-mismatch` (problem `details` names the mismatched fields; the attempt stays active and
nothing is recorded) when:

- `modelProvider`, `model`, or `deployment` differs from the pinned model selection's `provider`, `model`, or
  `deployment`;
- `modelSelectionRef` is present and differs from the pinned model selection digest;
- `parameters` is present and differs from the pinned model selection's `parameters` (absent pinned parameters equal
  `{}`).

The lineage `completed` entry and the accepted advice store `modelSelectionRef` and `parameters` when present. The
owner's `getAgentInvocation` and `getAgentInvocationLineage` return them inside the execution references; the OpenAPI
document and its committed snapshot are refreshed.

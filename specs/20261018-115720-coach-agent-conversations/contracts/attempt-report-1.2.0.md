# Contract: Attempt Report 1.2.0 (applied philosophy profile)

Change to `contracts/agent-orchestration/attempt-report/v1`, after release 1.1.0 of the
[Hermes Agent Runtime](../../20261018-113729-hermes-runtime/contracts/attempt-report-1.1.0.md). Releases 1.0.0 and 1.1.0
stay unchanged; `releases/1.2.0.schema.json` is added and the main schema becomes 1.2.0.

## Schema change

A closed definition `appliedPhilosophyProfile` is added and used in two places; every existing property and rule is
unchanged and additional properties stay refused:

- `checkpoint.appliedPhilosophyProfile` (optional property; the schema requires it when `kind` is `profile-rendered`);
- `completion.appliedPhilosophyProfile` (optional property).

```text
appliedPhilosophyProfile {
  renderingVersion: string  ^[a-z][a-z0-9-]{0,62}/[0-9]{1,4}$
  dimensionSet:     { id, version, digest }   # id kebab-case 3–100, version SemVer, digest ^sha-256:[0-9a-f]{64}$
  profile:          { id, version, digest }
  modifierPolicy:   { id, version, digest }
  baseWeights:      object, 1–32 properties, keys ^[a-z0-9]+(-[a-z0-9]+)*$, values integer 0–100
  modifiers:        array 0–5 of {
                      category: score-state | match-period | player-number-state | opponent-phase-behavior | evidence-availability,
                      deltas: object, 1–32 properties, same key pattern, values integer -100–100 except 0,
                      reason: string 1–300,
                      evidenceCallIds: array 1–10 unique of ^[A-Za-z0-9._\-]{1,128}$
                    }
  appliedWeights:   object, same constraints as baseWeights
}
```

Cross-field rules (budget, bounds, sums, evidence, pins) are platform checks, not schema rules.

Fixtures: valid `checkpoint-profile-rendered.json` (no modifiers), `checkpoint-profile-rendered-modifiers.json`,
`completion-coach-1.2.0.json`, and `completion-1.1.0-shape.json` (still valid); invalid
`checkpoint-profile-rendered-missing-snapshot.json`, `applied-profile-positional-weights.json` (array weights),
`applied-profile-unknown-category.json`, `applied-profile-reason-too-long.json`, and
`applied-profile-extra-property.json`.

## Platform behavior

`recordAgentCheckpoint` and `completeAgentAttempt` validate the object with the rules of
[data-model.md](../data-model.md#applied-philosophy-profile-attempt-report-120) against the invocation's pins, the
pinned profile and modifier policy, and the attempt's `agent_tool_calls`:

| Operation | Invalid object, object on a `user-to-specialist` attempt, or missing on a `user-to-coach` completion |
| --- | --- |
| `recordAgentCheckpoint` | `400 applied-philosophy-profile-invalid` with `violations: [{ code, pointer }]`; nothing recorded |
| `completeAgentAttempt` | `409 applied-philosophy-profile-invalid` with `violations`; nothing recorded; the attempt stays active |

Stale dispositions and operation-identifier rules take precedence unchanged. A valid checkpoint object is stored in
the lineage entry; a valid completion object is stored in the `completed` lineage entry and in
`agent_accepted_advice.applied_philosophy_profile`. Owner reads return it as described in
[coach-agent-interfaces.md](coach-agent-interfaces.md#member-reads).

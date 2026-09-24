# OpenSpec Change Queue

The OpenSpec change queue projects authoritative repository changes into GitHub
issues and processes explicitly enqueued, dependency-ready changes with GitHub
Copilot cloud agent.

This is repository tooling, not product CI. OpenSpec files remain authoritative.

## Authority and identity

Only active changes present on `main` receive issue twins and may be enqueued.
Each twin is classified by the `openspec:change` label and carries one managed
marker:

```text
<!-- openspec-change:v1
{"repository":"markusheiliger/socalytics","ref":"change-name","lifecycle":"active","gitRef":"main","path":"openspec/changes/change-name"}
-->
```

The canonical `ref` is stable identity. Lifecycle, Git ref, and path are mutable
projections. Archiving moves the change to a dated directory, so synchronization
must discover the actual archive path rather than derive one speculatively.

The issue is not authoritative for proposal, design, specifications, tasks, or
archive state. It links to those artifacts without copying them.

## Queue and dependency states

- `openspec:enqueued` records user authorization to process the normal
  no-warning OpenSpec lifecycle.
- Native GitHub `blocked by` relationships are the only dependency state.
- An issue is runnable only when it is open, enqueued, active on `main`, has no
  active Agent Task, needs no human decision, and every native blocker is closed
  with its archive confirmed on `main`.
- Open issues represent active or in-progress changes.
- Issues close as completed only after the archived pull request merges and the
  dated archive is observed on `main`.
- Closing as not planned is reserved for explicit cancellation or retirement.

Inferred dependencies never overwrite unowned native relationships. The queue
records the exact inferred edges it manages and may remove only those edges.
Manual edges remain intact.

Dependency candidates come from the read-only GitHub Agentic Workflow source in
`.github/workflows/openspec-change-dependencies.md`. Its generated
`.lock.yml` is committed with the source. The model can submit only one typed
custom safe output; the privileged job then applies the repository validator,
confidence threshold, whole-graph cycle check, and managed-edge provenance
rules before changing native relationships.

The controller starts Agent Tasks through the Agent Tasks API rather than
assigning the issue to Copilot. Native Copilot assignment is itself an immediate
execution trigger and would create a second, uncontrolled session that bypasses
the dependency and serialization checks.

## Cloud execution

The repository-owned `OpenSpec Cloud` agent executes one selected operation on
the durable implementation branch. It reads the generated OpenSpec skill for
that operation and does not replace or modify generated workflow instructions.

During apply, the parent invokes the hidden `soca-*` owner declared by each
task. Nested hidden-agent delegation was proven by the merged cloud-parity gate
in PR #2; its temporary probe agents were removed after the production
orchestrator replaced them.

The operation sequence is:

1. apply;
2. verify;
3. sync;
4. archive;
5. await human review and merge.

Every later Agent Task continues on the same open draft pull request by
providing both its base and head refs. Exactly one Agent Task may run for a
change at a time. Before each API dispatch, the controller persists a
`dispatching` checkpoint. An interrupted dispatch therefore stops safely
instead of starting an untracked duplicate task.

`openspec:enqueued` does not authorize bypassing warnings or making new product,
architecture, security, or archive decisions. A required interaction stops
processing for human attention.

## Durable evidence

Agent Task state alone is not success evidence. After every attempt, the
controller re-reads branch-visible OpenSpec state and appends an immutable
operation ledger comment containing:

- operation and attempt;
- Agent Task and session identifiers;
- branch and commit SHA before and after;
- outcome and validation summary; and
- recovery guidance for unsuccessful attempts.

A changed SHA is required when files changed. Read-only or already-satisfied
operations may retain the SHA only when operation-specific evidence proves the
result. Verify and sync also run strict OpenSpec validation in a detached
worktree at the exact recorded branch SHA. Sync additionally compares every
added, modified, or removed delta requirement and scenario with the accepted
specs on that branch before archive may start.

The Agent Tasks API does not expose the final response as a stable controller
contract. Reconciliation therefore validates persisted repository evidence.

## Failure and recovery

- `queued`, `in_progress`, `idle`, and `waiting_for_user` prohibit another
  dispatch. `waiting_for_user` is persisted as human attention rather than
  retried.
- `failed` and `timed_out` receive one automatic retry from the same observed
  checkpoint.
- `cancelled` and `waiting_for_user` are never retried automatically.
- A second failure records recovery guidance and stops.
- Closing the archive pull request without merging restores the issue projection
  to the active change on `main` and records a human-attention state.
- Reconciliation is level-triggered and idempotent; events wake it but do not
  authorize transitions by themselves.

## Human merge gate

After archive is committed and validated, automation stops. A human approves
workflow execution, marks the draft pull request ready, reviews it, and enables
auto-merge. The controller does not perform those actions.

Dependents remain blocked until the archived pull request merges, the dated
archive exists on `main`, the issue projection is updated to that path, and the
issue closes as completed.

## Repository-tooling validation

Run the pure queue contract tests from the repository root:

```powershell
node --test .github/scripts/*.test.mjs
gh aw validate openspec-change-dependencies.md
gh aw lint .github/workflows/openspec-change-dependencies.lock.yml
```

OpenSpec validation remains:

```powershell
openspec doctor --json
openspec schema validate spec-driven --json
openspec validate --all --json
openspec status --all --json
```

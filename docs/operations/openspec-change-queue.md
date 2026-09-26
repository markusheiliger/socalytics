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

- `openspec:change` classifies an issue twin.
- `openspec:enqueued` is a one-shot request to enter the queue. The controller
  consumes it when processing begins; it is not a persistent lifecycle state.
- `openspec:processing` marks an operation in progress.
- `openspec:stage:apply`, `openspec:stage:verify`, `openspec:stage:sync`, and
  `openspec:stage:archive` expose the current operation.
- `openspec:needs-attention` marks a stopped lifecycle that requires a human
  decision or recovery.
- `openspec:awaiting-review` marks a validated archive pull request awaiting
  the human merge gate.
- Native GitHub `blocked by` relationships are the only dependency state.
- An unstarted issue is initially runnable only when it is open, enqueued,
  active on `main`, needs no human decision, and every native blocker is closed
  with its archive confirmed on `main`. After admission, the persisted queue
  checkpoint—not a retained enqueue label—governs later operations, and no
  second Agent Task may overlap the active one.
- Open issues represent active or in-progress changes.
- Issues close as completed only after the archived pull request merges and the
  dated archive is observed on `main`.
- Closing as not planned is reserved for explicit cancellation or retirement.

There is no paused label or paused queue state. A run is processing, waiting on
authoritative blockers, awaiting review, complete, or stopped with
`openspec:needs-attention`. Reconciliation removes stale managed state and stage
labels. After the archived pull request merges and the archive is confirmed on
`main`, it removes the remaining transient queue labels and closes the issue;
`openspec:change` remains the issue-twin classifier.

Inferred dependencies never overwrite unowned native relationships. The queue
records the exact inferred edges it manages and may remove only those edges.
Manual edges remain intact.

## Client-side batch enqueue

The repository-owned `/opsx-enqueue [<change-ref> ...]` prompt and
`openspec-enqueue-change` skill provide equivalent entry points for initial
queue admission. Each contains the complete workflow, following the same
prompt-and-skill structure as the OpenSpec-provided operations. This is
client-side repository tooling; it does not invoke the queue scripts, dispatch
an Agent Task, assign an issue to Copilot, or create or repair issue twins and
dependencies.

With explicit refs, the operation validates those changes as a selected subset.
Without refs, it selects every eligible active change. In both cases it shows
the final dependency-complete order and requires explicit confirmation before
adding any queue labels.

A change is eligible only when:

- strict OpenSpec validation passes;
- its local directory is tracked and has no modified or untracked paths;
- its contents match `origin/main` and its latest affecting commit is reachable
  from `origin/main`;
- exactly one open active issue twin projects its canonical path from `main`;
  and
- it is not already enqueued, processing, awaiting review, completed, or
  stopped for human attention.

Eligible changes remain selectable when blocked. The skill walks native GitHub
`blocked by` relationships transitively. Closed OpenSpec blockers count as
satisfied only when their dated archive is confirmed on `main`; blockers
already admitted to the queue need not be selected again. If eligible blockers
are missing, the user may add the complete missing set or cancel. A manual,
malformed, cyclic, or otherwise ineligible unresolved blocker stops the batch.
The skill never edits dependency relationships.

After dependency closure, the skill shows the blocker-first order and requires
explicit confirmation. It then repeats the complete read-only preflight to
detect stale local or GitHub state before adding any label.

Label updates are idempotent but not atomic across issues. The skill applies
`openspec:enqueued` blocker-first, reports each issue result, skips dependents
whose selected blocker failed, and leaves successful labels in place because
the controller may already have consumed them. Rerunning `/opsx-enqueue` is the
recovery path for a partial failure.

## Combined issue reconciliation

One OpenSpec issue reconciliation Agentic Workflow replaces the separate issue
sync and dependency-inference workflows. References currently use the likely
source and generated names
`.github/workflows/openspec-change-reconciliation.md` and
`.github/workflows/openspec-change-reconciliation.lock.yml`; use the committed
source/lock pair if the implementation settles different names.

Every invocation performs deterministic issue synchronization first. This
creates or updates twins from authoritative state on `main`, projects active or
archived paths, and normalizes managed labels before any model is asked to infer
dependencies. AI inference therefore receives the synchronized active-change
set rather than stale issue projections.

Normal change-driven runs are incremental. They compare `openspec/changes/`
since the nearest valid dependency checkpoint and ask the model to evaluate only
affected active change refs while preserving unrelated managed edges. A weekly
scheduled run performs a full reconciliation of every active change. A manual
dispatch can request either full reconciliation or dry-run mode; dry-run reports
the deterministic and inferred actions without mutating issues, native
dependencies, labels, or the checkpoint.

The model has read-only repository and issue access and returns one typed custom
safe output. A privileged postprocessor independently validates identities,
evaluation coverage, evidence, confidence, duplicates, the complete graph for
cycles, and managed-edge provenance before changing native relationships.
Inference remains advisory: native GitHub `blocked by` relationships are the
only queue authority.

### Dependency checkpoint

Incremental inference uses the Git notes ref
`refs/notes/openspec-change-dependencies` as a rebuildable checkpoint and cache.
A note is attached to the exact reconciled commit and records summarized active
changes, the managed edge set, and inference metadata. It is not accepted
behavioral state, queue authority, or the sole record of a dependency.

Fetch the notes explicitly after cloning or before investigating an incremental
run:

```powershell
git fetch --no-tags origin refs/notes/openspec-change-dependencies:refs/notes/openspec-change-dependencies
```

Inspect the checkpoint attached to a commit with:

```powershell
git notes --ref=refs/notes/openspec-change-dependencies show <commit-sha>
```

The workflow may search ancestors for the nearest valid checkpoint. A missing,
stale, or invalid note causes a full rebuild; it must never cause native manual
blockers to be removed. Notes are produced by reconciliation and pushed
explicitly, not merged through pull requests or treated as human-authored
history.

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

Every Agent Task receives one versioned
`OPEN_SPEC_CLOUD_DISPATCH_V1=<json-object>` envelope. The initial apply uses a
`create` checkpoint containing the validated base ref and SHA. GitHub chooses
the implementation branch and may add an empty initial commit before the agent
starts, so the agent discovers that branch and proves the base SHA is an
ancestor of its HEAD. The controller does not guess a generated branch name or
HEAD SHA.

Every later Agent Task uses a `continue` checkpoint and continues on the same
open draft pull request by providing the exact base ref, head ref, and starting
head SHA. The agent requires its checked-out branch and HEAD to match before
editing. Exactly one Agent Task may run for a change at a time. Before each API
dispatch, the controller persists a `dispatching` checkpoint. An interrupted
dispatch therefore stops safely instead of starting an untracked duplicate
task.

The controller consumes `openspec:enqueued` when it starts processing. Each
later controller-selected operation on the same durable lifecycle is authorized
by the validated queue checkpoint and explicit dispatch; the cloud agent must
not require the one-shot label to remain. Neither initial queueing nor a later
dispatch authorizes bypassing warnings or making new product, architecture,
security, or archive decisions. A required interaction stops processing for
human attention.

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
- Recovering a failed initial dispatch requires preserving its immutable ledger
  evidence, closing its empty draft pull request without merging, deleting only
  that generated branch, clearing only the mutable checkpoint and transient
  queue labels, and explicitly re-enqueuing the active change after the
  controller fix is present on `main`.
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
gh aw validate .github/workflows/openspec-change-reconciliation.md
gh aw lint .github/workflows/openspec-change-reconciliation.lock.yml
node .github/scripts/openspec-change-workflow-names.mjs --check
```

The workflow paths are the approved likely names, not a requirement to rename
an implementation that settles another committed source/lock pair. Compile the
Markdown source with `gh aw compile`, then run
`node .github/scripts/openspec-change-workflow-names.mjs` to apply the
repository's deterministic display names for compiler-generated jobs. Never
hand-edit the generated lock file.

OpenSpec validation remains:

```powershell
openspec doctor --json
openspec schema validate spec-driven --json
openspec validate --all --json
openspec status --all --json
```

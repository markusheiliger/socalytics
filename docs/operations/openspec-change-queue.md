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
<!-- openspec-json
{"$schema":".github/scripts/schemas/change-marker-v1.schema.json","repository":"markusheiliger/socalytics","ref":"change-name","lifecycle":"active","gitRef":"main","path":"openspec/changes/change-name"}
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

The generated OOTB `openspec` agent executes one selected operation on the
durable implementation branch. The controller names the binding generated
OpenSpec skill in its prompt and does not replace or modify generated agent or
skill instructions.

During apply, the controller selects one unchecked numbered OpenSpec task and
persists its task id, normalized capability set, and the ids of tasks that were
already complete. Capability ids resolve directly to
`openspec/capabilities/<id>.md`; no registry, inference, or specialist agent is
involved. The controller rejects unknown capabilities, unsupported operations,
exclusive multi-capability sets, and incompatible mutation, isolation, or
result-schema contracts before dispatch.

One Agent Task executes that exact task under the complete compatible
capability set, marks only that task complete, validates it, and ends at a new
checkpoint commit pushed to the remote pull-request branch. If unchecked tasks
remain, the controller dispatches another apply Agent Task from that exact
checkpoint on the same branch and pull request before advancing to verify.
This task-level checkpoint keeps each cloud session bounded and makes completed
work durable before the next task starts.

The operation sequence is:

1. apply;
2. verify;
3. sync;
4. archive;
5. await human review and merge.

Every Agent Task receives one standalone JSON dispatch object whose `$schema`
is `.github/scripts/schemas/queue-dispatch-v1.schema.json`. The initial apply uses a
`create` checkpoint containing the validated base ref and SHA. GitHub chooses
the implementation branch and may add an empty initial commit before the agent
starts, so the agent discovers that branch and proves the base SHA is an
ancestor of its HEAD. The controller does not guess a generated branch name or
HEAD SHA.

Apply envelopes also contain the selected task block, sorted capability ids,
direct capability paths, and validated effective policy. Every operation must
create a final commit with exactly one `OpenSpec-JSON:` trailer
whose one-line schema-bearing JSON identifies the change, operation, selected apply
task when applicable, verdict, and validation summary. The agent pushes that
commit and verifies the remote head before responding. Apply's final assistant
response also contains only one JSON object with
`"$schema": "openspec/capabilities/schemas/capability-result-v1.schema.json"`
conforming to
`openspec/capabilities/schemas/capability-result-v1.schema.json`. Verify, sync,
and archive use the same JSON-only transport with
`"$schema": ".github/scripts/schemas/operation-result-v1.schema.json"`
conforming to that path. These schema paths are controller-owned
repository-relative identifiers, not agent-supplied paths or URLs. Chat output is
supplemental diagnostics: malformed or unavailable output is recorded but does
not override a valid pushed checkpoint. Reconciliation accepts a result only
when the checkpoint identity matches, the remote branch is strictly ahead of
the controller checkpoint, that exact task changed to complete, every task that
was previously complete remains complete, and no other pending task was
completed by the operation. The controller reads changed paths and validation
evidence at the immutable final commit and rechecks the remote head before
crediting it. A successful
intermediate apply result stays in the apply stage with attempt 1 for the next
selected task. A retry retains the same selected task and increments only that
task's attempt.

`verification` and `audit` are exclusive, isolated capabilities. Their
task-level operation may change only the selected checkbox in the active
`tasks.md`; any other file mutation is rejected. Lifecycle verify runs in a
fresh Agent Task, changes no files, and creates and pushes an explicit empty
checkpoint commit after a passing assessment.

Every later Agent Task uses a `continue` checkpoint and continues on the same
open draft pull request by providing the exact base ref, head ref, and starting
head SHA. The agent requires its checked-out branch and HEAD to match before
editing. Exactly one Agent Task may run for a change at a time. Before each API
dispatch, the controller persists a `dispatching` checkpoint. An interrupted
dispatch therefore stops safely instead of starting an untracked duplicate
task.

The workflow runs reconciliation in watch mode while an Agent Task is queued,
in progress, or idle. It polls the Agent Tasks API once per minute, dispatches
the next task or lifecycle operation as soon as durable evidence passes, and
exits when the queue reaches human review, blocking, or attention. This is the
primary progression mechanism. Pull-request events and the scheduled trigger
remain recovery wake-ups; lifecycle progress does not depend on Copilot-created
pull-request events being approved or on scheduled workflows running exactly at
their requested cron interval.

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

- operation, selected apply task when applicable, and attempt;
- Agent Task and session identifiers;
- branch and commit SHA before and after;
- outcome and validation summary; and
- recovery guidance for unsuccessful attempts.

A new pushed SHA is required for every successful operation. Read-only or
already-satisfied operations use an empty checkpoint commit. Verify and sync
also run strict OpenSpec validation in a detached worktree at the exact
recorded branch SHA. Sync additionally compares every
added, modified, or removed delta requirement and scenario with the accepted
specs on that branch before archive may start.

The controller may reconstruct the final assistant response as supplemental
diagnostics, but the pushed checkpoint commit and controller-owned repository
validation are authoritative. After any terminal Agent Task state, the
controller captures the branch head, validates its checkpoint, reads task and
capability evidence at that immutable commit, and rechecks the branch head
before crediting the operation. A branch that moves during this validation is
treated as transient settling: reconciliation waits and retries without
consuming the operation retry, writing a failure ledger, or applying
`openspec:needs-attention`.

## Failure and recovery

- `queued`, `in_progress`, `idle`, and `waiting_for_user` prohibit another
  dispatch. `waiting_for_user` is persisted as human attention rather than
  retried.
- `failed` and `timed_out` receive one automatic retry. When the branch did not
  advance, the retry uses the previous checkpoint. When the branch contains
  pushed partial progress but no final checkpoint, the controller first proves
  the selected task remains unchecked, credited tasks did not regress, and the
  capability identity is unchanged, then retries from the new branch head.
- A terminal Agent Task with a valid pushed checkpoint is credited after normal
  controller validation even when GitHub's outer task or post-processing state
  is failed.
- A terminal Agent Task whose branch advanced without a valid checkpoint never
  retries from a stale SHA. It may use its single retry from the observed
  partial-progress head; after attention, an operator can invoke the guarded
  `resume_issue` workflow input to continue that same unchecked task from its
  current pushed head.
- `cancelled` and `waiting_for_user` are never retried automatically.
- A second failure records recovery guidance and stops.
- Terminal evidence failures update one managed attention comment with a stable
  failure code, expected and observed evidence when applicable, branch and
  controller checkpoints, already credited apply tasks, Agent Task and pull
  request links, a link to the immutable ledger entry, and an exact bounded
  recovery action. Raw assistant responses and unbounded exception content are
  never published.
- Closing the archive pull request without merging restores the issue projection
  to the active change on `main` and records a human-attention state.
- Recovering a failed initial dispatch requires preserving its immutable ledger
  evidence, closing its empty draft pull request without merging, deleting only
  that generated branch, clearing only the mutable checkpoint and transient
  queue labels, and explicitly re-enqueuing the active change after the
  controller fix is present on `main`.
- A full queue reset follows the same evidence-preserving rule: first quiesce
  workflow and Agent Task activity, close generated pull requests without
  merging, delete only verified unreferenced generated branches, remove
  transient queue labels and mutable queue-state or attention comments, and
  preserve issue twins, native dependencies, and immutable operation ledgers
  before explicitly re-enqueuing.
- After the failed pull request is closed without merging, run the queue
  workflow manually with `reset_issue` set to the issue number. The guarded
  reset accepts only `needs_attention`, a closed unmerged PR, and a terminal
  Agent Task; it deletes only bot-owned mutable state and attention comments
  and removes managed queue labels. It never removes ledger comments or
  re-enqueues the issue.
- For a useful open pull request stopped after pushed partial progress, run the
  queue workflow manually with `resume_issue` set to the issue number. The
  guarded resume accepts only an open `needs_attention` issue, its existing
  open durable PR, a terminal Agent Task, a branch head newer than the
  controller checkpoint, and the same apply task with no credited-task
  regression. It removes the mutable attention comment. An unchecked task
  dispatches the next attempt from the observed branch head; an already-checked
  task returns to `dispatched` only for controller revalidation of its durable
  checkpoint. Both paths remain in the normal watch/reconcile loop so
  successful completion is credited and the remaining apply and lifecycle
  operations continue in the same sequence.
- Reconciliation is level-triggered and idempotent; events wake it but do not
  authorize transitions by themselves.

## JSON contract migration

Repository-owned queue payloads identify their contract with an exact
repository-relative `$schema`. Normal controllers reject legacy version fields,
short schema identifiers, and versioned framing.

For the one-time cutover, run the queue workflow manually with
`migrate_json_contracts` enabled. The migration runs under the queue workflow's
normal concurrency lock, stops before mutation when an affected Agent Task is
active, updates current issue markers and mutable state comments, replaces the
current dependency Git note, and appends a schema-bearing checkpoint commit
when the authoritative branch head still uses the legacy trailer. It does not
rewrite historical ledger comments or commit history.

The script defaults to a read-only report when run directly:

```powershell
node .github/scripts/openspec-json-migration.mjs
```

Only the guarded workflow supplies `--apply` and the required write
permissions.

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

# OpenSpec Change Processing

Exploration and proposals happen client-side, for example in VS Code with
`/opsx-explore` and `/opsx-propose`. Once a change is committed to `main`, its
implementation (apply, verify, sync, archive) runs server-side in the single
`openspec` workflow, so several engineers can have changes processed in
parallel without running long OpenSpec sessions locally.

This is repository tooling, not product CI. OpenSpec files remain authoritative.

## Overview

1. Every active change on `main` gets a GitHub issue twin.
2. A user with write access adds the `openspec:enqueued` label to the twin (by
   hand or with `/opsx-enqueue`).
3. When all native blockers are archived on `main`, the workflow creates the
   branch `openspec/<change>` from `main` with an empty start commit and opens a
   draft pull request.
4. The workflow starts one fresh Copilot agent session per numbered apply task,
   then one each for verify, sync, and archive. Every session works on the
   draft pull request branch and ends with a pushed checkpoint commit.
5. Each checkpoint push wakes the workflow, which validates the result and
   starts the next session. When it needs a human, it asks on the pull request
   and waits.
6. After the archive is validated on the branch, a human marks the pull request
   ready, reviews it, and merges it. The archive reaches `main` with the merge,
   and the workflow closes the issue as completed.

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

## Issue: queue state

The issue only holds the request to process a change. Labels are the only
mechanism, because they can be set by users and trigger workflows; the repository
is user-owned, so issue types and issue fields are unavailable, and Projects
changes cannot trigger workflows.

| Label | Set by | Meaning |
| --- | --- | --- |
| `openspec:change` | twin sync | Classifies the issue twin. |
| `openspec:enqueued` | human | One-shot request to process the change. Removed by the workflow when processing starts. |
| `openspec:processing` | workflow | A draft pull request is processing the change. |
| `openspec:needs-attention` | workflow | The pull request waits for a human, or a previous pull request was closed without merging. |
| `openspec:awaiting-review` | workflow | The change is archived on its pull request and waits for human review and merge. |

- Only `openspec:enqueued` is input. The other labels are outputs that every run
  rewrites, so editing them by hand changes nothing.
- The workflow honors `openspec:enqueued` only when the user who applied it has
  write, maintain, or admin access. Otherwise it removes the label and explains
  why on the issue.
- Removing the label before processing starts withdraws the request. Adding it
  to a change that is already processing is ignored and removed with a note.
- Native GitHub `blocked by` relationships are the only dependency authority.
  An enqueued change starts only when every blocker is closed and, for an
  OpenSpec twin, its archive is projected from `main`.
- The optional repository variable `OPENSPEC_MAX_ACTIVE_CHANGES` limits how
  many changes are processed at once. Without it there is no limit.
- The issue receives three kinds of short comments: when processing starts,
  when a gate opens, and when the change is merged and archived.

## Client-side batch enqueue

The repository-owned `/opsx-enqueue [<change-ref> ...]` prompt and
`openspec-enqueue-change` skill provide equivalent entry points for requesting
processing. Each contains the complete workflow, following the same
prompt-and-skill structure as the OpenSpec-provided operations. This is
client-side repository tooling; it does not invoke the workflow scripts, dispatch
an Agent Task, assign an issue to Copilot, or create or repair issue twins and
dependencies.

With explicit refs, the operation validates those changes as a selected subset.
Without refs, it selects every eligible active change. In both cases it shows
the final dependency-complete order and requires explicit confirmation before
adding any labels.

A change is eligible only when:

- strict OpenSpec validation passes;
- its local directory is tracked and has no modified or untracked paths;
- its contents match `origin/main` and its latest affecting commit is reachable
  from `origin/main`;
- exactly one open active issue twin projects its canonical path from `main`;
  and
- it is not already enqueued, processing, or awaiting review, and it was not
  stopped (`openspec:needs-attention` without `openspec:processing`) unless its
  ref was supplied explicitly.

Eligible changes remain selectable when blocked. The skill walks native GitHub
`blocked by` relationships transitively. Closed OpenSpec blockers count as
satisfied only when their dated archive is confirmed on `main`; blockers
already enqueued or processing need not be selected again. If eligible blockers
are missing, the user may add the complete missing set or cancel. A manual,
malformed, cyclic, or otherwise ineligible unresolved blocker stops the batch.
The skill never edits dependency relationships.

After dependency closure, the skill shows the blocker-first order and requires
explicit confirmation. It then repeats the complete read-only preflight to
detect stale local or GitHub state before adding any label.

Label updates are idempotent but not atomic across issues. The skill applies
`openspec:enqueued` blocker-first, reports each issue result, skips dependents
whose selected blocker failed, and leaves successful labels in place because
the workflow may already have consumed them. Rerunning `/opsx-enqueue` is the
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
only dependency authority.

### Dependency checkpoint

Incremental inference uses the Git notes ref
`refs/notes/openspec-change-dependencies` as a rebuildable checkpoint and cache.
A note is attached to the exact reconciled commit and records summarized active
changes, the managed edge set, and inference metadata. It is not accepted
behavioral state, processing authority, or the sole record of a dependency.

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

## The `openspec` workflow

`.github/workflows/openspec.yml` is the only processing workflow. It is
event-driven and short-lived: each run reads the current state, starts at most
one agent session per change, and exits. Nothing polls.

### Triggers

| Event | Why |
| --- | --- |
| `issues` (labeled, unlabeled, closed, reopened) | Enqueue requests and cancellations. |
| `pull_request_target` (synchronize) | An agent pushed a checkpoint. This event always runs the workflow file from `main`. |
| `pull_request_target` (closed) | A processing pull request was merged or closed. |
| `issue_comment` (created) | A `/openspec` command on a pull request. |
| `push` to `main` under `openspec/changes/**` | Archives on `main` unblock dependents and complete finalization. |
| `schedule` (every 15 minutes) | Watchdog for sessions that ended without pushing. |
| `workflow_dispatch` | Manual run; `dry_run` only reports what would happen. |

Events only wake the workflow. Every run reconciles every change from
authoritative state, so no event authorizes a transition by itself. Relevant
runs share one repository-wide concurrency group; GitHub keeps at most one
pending run, and a replaced pending run loses nothing. Irrelevant events, such
as ordinary comments, get their own concurrency group so they never replace a
relevant pending run.

### Jobs

Each job has a readable name, and matrix jobs show the change in parentheses,
for example `Apply next task (add-club-identity-foundation)`.

| Job | Display name | What it does |
| --- | --- | --- |
| `observe` | Read current state | Applies `/openspec` commands, recovers interrupted dispatches, records out-of-session pushes, and finds finished sessions. |
| `credit` | Check agent result | Per change: validates the checkpoint commit and records the outcome. |
| `plan` | Decide next steps | Decides the next step for every change and emits one list per step. |
| `admit` | Start change | Creates the branch and draft pull request, then starts the first task. |
| `apply` | Apply next task | Starts an agent session for the next unchecked task. |
| `verify` | Verify change | Starts an agent session that verifies the change. |
| `sync` | Sync specs | Starts an agent session that synchronizes delta specs. |
| `archive` | Archive change | Starts an agent session that archives the change on the branch. |
| `gate` | Ask for human input | Posts the question on the pull request and notifies the issue. |
| `finalize` | Finish change | Records a merge, a close without merge, or an abort. |
| `publish` | Update PR and issue status | Refreshes overview comments and labels. |

Operation jobs run as matrices with `fail-fast: false`, so one broken change
never blocks another. Each run title states its trigger, for example
`openspec · enqueued #4 by @alice` or `openspec · watchdog`.

## Pull request: processing state and change log

Each admitted change gets a draft pull request created by the workflow from
`openspec/<change>` (or `openspec/<change>-r<n>` when an earlier attempt's
branch still exists) into `main`.

| Artifact | Written by | Read as state | Role |
| --- | --- | --- | --- |
| `OpenSpec lifecycle` check run on the head commit | workflow | yes, the only processing state | Current state and status. |
| Checkpoint commits with an `OpenSpec-JSON:` trailer | agent | yes, as evidence | Proof of the work done. |
| Per-operation check runs, for example `OpenSpec apply 2.1` | workflow | no | Validation report on the commit it validated. |
| Overview comment (first comment) | workflow | no | Stage table, tasks, current step, and next action. |
| Numbered change-log comments | workflow | no | The human-readable story of the change. |
| `/openspec` command comments | humans | once each | Gate decisions. |
| `openspec:stage:*` label | workflow | no | Current stage at a glance. |

### State in the lifecycle check run

The processing state is JSON conforming to
`.github/scripts/schemas/change-run-state-v1.schema.json`, stored in the
`output.text` of the `OpenSpec lifecycle` check run. Users and the agent cannot
edit check runs; only the GitHub Actions app that created them can. The workflow
accepts a state only from a check run created by `github-actions`, with
`external_id` equal to the change and a matching pull request number.

- Reading walks first parents back from the pull request head and uses the
  nearest valid state. Commits newer than that are new work to credit. The walk
  has no 250-commit limit and stops at the start commit.
- Writing updates the run on the current head or creates a new run when the head
  moved. The previous run is marked superseded and its state is removed, so
  exactly one valid state exists and a branch reset can never bring back an
  older state.
- State JSON is stored compactly and must fit the 65,535-character check-run
  limit. Only the last five human answers are kept in state and passed to the
  agent; every answer stays in the change log.
- The check is `in_progress` while a session is starting or running,
  `action_required` while a gate is open, `success` only after a validated
  archive, and `cancelled` after an abort or close without merge. The check-run
  summary shows the same overview as the first comment.
- The lifecycle check is informational. If branch protection is configured
  later, requiring `OpenSpec lifecycle` blocks merging an OpenSpec pull request
  until its archive is validated.
- If a pull request has no valid state (for example after a force-push removed
  it), the workflow posts a note and stops processing that pull request. Close
  it without merging and re-enqueue the issue to start over. A branch reset to
  its start commit restores the initial state instead, without replaying
  earlier commands.

### Change log

The Conversation tab reads top to bottom as the story of the change:

- The overview comment comes first and is regenerated on every run.
- Every milestone gets a numbered entry: start, each agent session, each gate,
  each accepted or rejected command, out-of-session pushes, and the merge,
  close, or abort.
- A session entry is posted as running and edited once with its result, so
  starts and finishes do not produce separate comments.
- Entries use one format: a heading with the step and outcome, a few plain
  sentences, the agent's own summary from the checkpoint (sanitized: mentions,
  HTML, headings, and links outside the repository are neutralized), evidence
  links to the session and commits, the next step, and collapsed details.
- Only gate entries mention someone: the user who enqueued the change.
- Runs that find nothing new post nothing.

Comments are display only. Editing them changes nothing; the next run
regenerates the overview.

## Agent sessions

The workflow starts sessions with the Agent Tasks API using the generated
`openspec` custom agent, `base_ref: main`, and `head_ref` set to the pull
request branch. It never assigns issues to Copilot, because assignment starts an
uncontrolled extra session.

- One session runs per apply task, and one each for verify, sync, and archive.
  No session is reused.
- The prompt embeds a dispatch envelope conforming to
  `.github/scripts/schemas/change-dispatch-v2.schema.json`: the change, the
  operation, the expected head SHA, the attempt, the selected task with its
  capability paths and full task block, and every human answer so far.
- The agent must confirm the expected head before editing, follow the binding
  OpenSpec skill, never ask questions in chat, and push progress early.
- Every session, successful or not, ends with one pushed commit whose message
  has exactly one `OpenSpec-JSON:` trailer conforming to
  `.github/scripts/schemas/change-checkpoint-v2.schema.json`:

```text
OpenSpec-JSON: {"$schema":".github/scripts/schemas/change-checkpoint-v2.schema.json","change":"add-club","operation":"apply","task":"2.1","verdict":"complete","summary":"Added the Club commands and queries.","validation":"dotnet test: 14 passed"}
```

| Verdict | Meaning | Workflow response |
| --- | --- | --- |
| `complete` | The step is done. | Validate, then credit and continue. |
| `partial` | Progress was pushed but the step is unfinished. | Retry from the new head. |
| `needs_decision` | A human decision is needed; `question` states it with a recommendation. | Decision gate. |
| `failed` | The step could not be done. | Retry once, then failure gate. |

A complete verify also reports `findings` with exact critical, warning, and
suggestion counts and up to 20 listed findings.

### Validation before crediting

A `complete` checkpoint is credited only after the workflow validates it at that
exact commit. Changes are compared with the head before the first attempt of the
step, moved forward only by commits pushed while no session runs, so a failed
or partial attempt cannot hide changes from a later attempt:

- Apply: the selected task is checked, no other checkbox changed, no task was
  added or removed, and checkbox-only capabilities (`verification`, `audit`)
  changed nothing but `tasks.md`. Strict `openspec validate` passes for the
  change.
- Verify: no files changed and strict validation passes.
- Sync: strict validation passes and every delta requirement matches the
  accepted specs on the branch.
- Archive: the active change directory is gone, a dated archive directory
  exists, and strict spec validation passes.

A checkpoint for a different change, operation, or task, or a malformed
trailer, counts as a failed attempt.

## Gates and commands

| Gate | Opens when | Commands |
| --- | --- | --- |
| Decision | The agent reports `needs_decision`, or its session waits for input. | `/openspec answer <text>`, `/openspec approve` (accept the agent's recommendation), `/openspec abort` |
| Review | Verify found suggestions or warnings and no critical issue. | `/openspec approve` (continue to sync), `/openspec retry` (verify again, for example after pushing fixes), `/openspec abort` |
| Failure | A second failed attempt, a cancelled session, invalid branch history, a closed issue, or any critical verify finding. | `/openspec retry`, `/openspec abort` |
| Merge | The archive is validated. | None. A human marks the pull request ready, reviews it, and merges it. |

A clean verify with no findings continues to sync without a gate. Critical
findings cannot be approved, because archive guidance forbids archiving with
blocking issues.

Commands:

- must start a pull request comment with `/openspec`;
- are accepted only from users with write, maintain, or admin access, and bot
  comments are ignored;
- are processed once, in order, on comment events, watchdog and manual runs,
  and on any run while a gate is waiting; edits after processing have no
  effect, and a command edited before processing is rejected with a request to
  post it again;
- are acknowledged with a 👍 reaction and a change-log entry that records who
  decided what, or a 😕 reaction and an entry explaining the rejection.

`/openspec abort` works at any time. The workflow closes the pull request
without merging and returns the issue to idle; re-adding `openspec:enqueued`
starts over on a new branch.

## Retries and recovery

- A `partial` or `failed` verdict, an invalid checkpoint, or a session that
  ended without a checkpoint gets one retry in a new session from the current
  head. A second failure opens a failure gate.
- A cancelled session opens a failure gate without retrying.
- Before calling the Agent Tasks API, the workflow records a `dispatching` state.
  If a run dies there, the next run looks for the session on the branch: it
  adopts exactly one match, starts again after 10 minutes without a match, and
  opens a failure gate when several match.
- If the Agent Tasks API rejects a start, a failure gate shows the error;
  `/openspec retry` tries again.
- Commits pushed while no session is running are recorded in the change log and
  become the starting point of the next step.
- Closing the pull request without merging stops processing and marks the issue
  `openspec:needs-attention`; re-adding `openspec:enqueued` starts over.

## Repository setup

These one-time settings are required for server-side processing:

1. **Let Copilot pushes run workflows.** In the Copilot coding agent settings,
   turn off the approval requirement for workflows triggered by Copilot.
   Otherwise each checkpoint push waits for a human to approve the run, and only
   the 15-minute watchdog advances processing.
2. **Create the `openspec` environment.** Limit its deployment branches to
   `main`, add the `COPILOT_AGENT_TOKEN` secret there (a fine-grained personal
   access token with Agent tasks read and write permission; installation tokens
   are not supported by the Agent Tasks API), and remove the repository-level
   secret. Workflows from other branches then cannot read the token.
3. **Optionally** set the repository variable `OPENSPEC_MAX_ACTIVE_CHANGES`, and
   require the `OpenSpec lifecycle` check in branch protection.

The workflow uses `GITHUB_TOKEN`, which is limited to 1,000 API requests per
hour per repository. To stay within it, each run reads state from the head
commit, scans only pull requests closed in the last 24 hours for finalization,
and refreshes overview comments and labels only for changes the run touched.
Use `OPENSPEC_MAX_ACTIVE_CHANGES` if many changes run in parallel.

## Human merge gate

After archive is committed and validated, automation stops. A human marks the
draft pull request ready, reviews it, and merges it. The workflow does not
perform those actions.

Dependents remain blocked until the pull request merges, the dated archive
exists on `main`, the issue projection is updated to that path, and the issue
closes as completed.

## Repository-tooling validation

Run the contract tests from the repository root:

```powershell
node --test .github/scripts/*.test.mjs
gh aw validate .github/workflows/openspec-change-reconciliation.md
gh aw lint .github/workflows/openspec-change-reconciliation.lock.yml
node .github/scripts/openspec-change-workflow-names.mjs --check
```

`openspec-workflow.test.mjs` enforces that every job and step in
`openspec.yml` has a readable name and that the workflow keeps trusted
checkouts, least privilege, and the repository-wide lock. Compile the
reconciliation Markdown source with `gh aw compile`, then run
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

# OpenSpec Change Processing

Exploration and proposals happen client-side, for example in VS Code with
`/opsx-explore` and `/opsx-propose`. Once a change is committed to `main`, its
implementation (apply, verify, sync, archive) runs server-side in the single
OpenSpec orchestrator, so several engineers can have changes processed in
parallel without running long OpenSpec sessions locally.

This is repository tooling, not product CI. OpenSpec files remain authoritative.

## Overview

1. Every active change on `main` gets a GitHub issue twin.
2. A user with write access adds the `openspec:enqueued` label to the twin (by
   hand or with `/opsx-enqueue`).
3. When all native blockers are archived on `main`, the workflow creates the
   branch `openspec/<change>` from `main` with an empty start commit and opens a
   draft pull request.
4. The workflow starts one fresh agent session per numbered apply task,
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

## OpenSpec prepare

The **OpenSpec prepare** Agentic Workflow
(`.github/workflows/openspec-prepare.md`, compiled to
`.github/workflows/openspec-prepare.lock.yml`) prepares changes for
processing: it synchronizes issue twins and infers dependencies between
changes.

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

## The OpenSpec orchestrator workflow

`.github/workflows/openspec-orchestrator.yml` is the only processing workflow. It is
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
| `verify-checkpoint` | Verify checkpoint (*change*, *sha*) | Per complete apply checkpoint: calls the repository's verification workflow with a read-only token and no secrets (see [Repository-specific verification and tooling](#repository-specific-verification-and-tooling)). |
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
`OpenSpec orchestrator · enqueued #4 by @alice` or `OpenSpec orchestrator · watchdog`.
Agent runs name the pull request, branch, step, and dispatch id, for example
`OpenSpec agent · #24 openspec/<change> · apply 2.3 (attempt 2) · <dispatch id>`.
The step is display-only; the agent builds its prompt from the lifecycle state.

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

Sessions run on one of two runtimes, selected by the repository variable
`OPENSPEC_AGENT_RUNTIME` and recorded per session in the state:

| | `actions` (default) | `copilot` (fallback) |
| --- | --- | --- |
| Where | The `openspec-agent` agentic workflow (`.github/workflows/openspec-agent.md`), Copilot engine, 60-minute budget | A Copilot cloud agent session through the Agent Tasks API (about 10.5-minute limit) |
| Started by | `workflow_dispatch` with only the pull request number and a dispatch id | `POST /agents/repos/{owner}/{repo}/tasks` with `head_ref` set to the pull request branch |
| Credentials | `GITHUB_TOKEN` with `copilot-requests: write`; no personal access token | `COPILOT_AGENT_TOKEN` fine-grained personal access token |
| Pushing | The agent commits locally; gh-aw's push job publishes the commits only to the dispatched pull request, refusing `.github/` and protected files | The agent pushes itself |
| Docker | Not in the sandbox. The repository's optional host-side `run_verification` tool, off unless `OPENSPEC_AGENT_HOST_TESTS=true` | Inside the session |
| Waking the controller | The agent's final `wake_controller` call starts `openspec-orchestrator.yml` after the push | The push's `pull_request_target` event |

The `actions` runtime reads everything except the pull request number and
dispatch id from the pull request's lifecycle state in a trusted step, so it
executes exactly what the controller dispatched. It refuses to run when the
dispatch id no longer matches or the branch moved after the dispatch.

**Security trade-off of `run_verification`:** the tool's command is fixed,
but it runs test code the agent wrote on the runner host, outside the sandbox's
network firewall and with access to the runner's Docker. That code could send
data out and, through Docker, read the agent job's tokens (read-only repository
access and `copilot-requests: write`). The tool therefore stays off unless the
repository variable `OPENSPEC_AGENT_HOST_TESTS` is `true`; it runs the tests
with a minimal environment and a 14-minute limit. Without it, the agent relies
on the binding verification job and on its feedback passed to the next attempt.

The workflow never assigns issues to Copilot, because assignment starts an
uncontrolled extra session.

- One session runs per apply task, and one each for verify, sync, and archive.
  No session is reused.
- The prompt embeds a dispatch envelope conforming to
  `.github/scripts/schemas/change-dispatch-v2.schema.json`: the change, the
  operation, the expected head SHA, the attempt, the selected task with its
  capability paths and full task block, every human answer so far, and the
  previous attempt's failure details on a retry.
- The agent must confirm the expected head before editing, follow the binding
  OpenSpec skill, never ask questions in chat, and save progress early.
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
  change. The repository's verification passes at the checkpoint. It runs in
  the **Verify checkpoint** job, which calls the repository's reusable
  verification workflow with a read-only token and no secrets, because it
  executes agent-written code. "Check agent result" reads only the conclusions
  of those jobs from the Actions API. When verification fails, the
  `verification.txt` it uploaded becomes the feedback for the next attempt.
  Because the verification jobs belong to the orchestrator run on `main`, their
  own checks never appear on the pull request; "Check agent result" mirrors the
  result as an **OpenSpec verification** check on the checkpoint commit, with
  that feedback and a link to the job log. The operation check and the
  change-log entry show the same details for retries and failure gates.
- Verify: no files changed and strict validation passes.
- Sync: only `openspec/` and `docs/` changed, strict validation passes, and
  every delta requirement matches the accepted specs on the branch.
- Archive: only `openspec/` and `docs/` changed, the active change directory is
  gone, a dated archive directory exists, and strict spec validation passes.

A checkpoint for a different change, operation, or task, or a malformed
trailer, counts as a failed attempt.

## Gates and commands

| Gate | Opens when | Commands |
| --- | --- | --- |
| Decision | The agent reports `needs_decision`, or its session waits for input. | `/openspec answer <text>`, `/openspec approve` (accept the agent's recommendation), `/openspec abort` |
| Review | Verify found suggestions or warnings and no critical issue. | `/openspec approve` (continue to sync), `/openspec retry` (verify again, for example after pushing fixes), `/openspec abort` |
| Failure | A second failed attempt, a cancelled session, invalid branch history, a closed issue, or any critical verify finding. | `/openspec retry [guidance]` (start over with fresh attempts; text after the command goes to the agent ahead of the last failure details), `/openspec abort` |
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
- Before starting a session, the workflow records a `dispatching` state with a
  dispatch id. If a run dies there, or GitHub doesn't return the new run's id,
  the next run looks for the session (an `openspec-agent` run whose name
  contains the dispatch id, or a Copilot session on the branch): it adopts
  exactly one match, starts again after 10 minutes without a match, and opens a
  failure gate when several match.
- If starting a session fails, a failure gate shows the error;
  `/openspec retry` tries again.
- An agentic run that ends without calling `wake_controller` (for example a
  crash) is picked up by the 15-minute watchdog.
- Commits pushed while no session is running are recorded in the change log and
  become the starting point of the next step.
- Closing the pull request without merging stops processing and marks the issue
  `openspec:needs-attention`; re-adding `openspec:enqueued` starts over.

## Repository setup

These one-time settings are required for server-side processing:

1. **Let GitHub Actions create pull requests.** In **Settings → Actions →
   General → Workflow permissions**, enable **Allow GitHub Actions to create and
   approve pull requests** and keep the default permissions read-only. Without
   it, admission fails with `GitHub Actions is not permitted to create or
   approve pull requests`. The workflow only opens and closes its own draft pull
   requests; it never approves.
2. **For the `copilot` runtime only: let Copilot pushes run workflows.** In
   **Settings → Copilot → Cloud agent → Actions workflow approval**, turn off
   **Require approval for workflow runs**. Otherwise each checkpoint push creates
   a held run that someone must release with **Approve and run workflows** on the
   pull request, or that waits for the watchdog. The `actions` runtime doesn't
   need this.
3. **For the `copilot` runtime only: create the `openspec` environment.** Limit its deployment branches to
   `main`, add the `COPILOT_AGENT_TOKEN` secret there (a fine-grained personal
   access token with Agent tasks read and write permission; installation tokens
   are not supported by the Agent Tasks API), and remove the repository-level
   secret. Workflows from other branches then cannot read the token. Without
   this step GitHub creates the environment on first use without restrictions
   and the repository-level secret is used.
4. **Optionally** set the repository variables `OPENSPEC_AGENT_RUNTIME`
   (`actions` by default, or `copilot`), `OPENSPEC_MAX_ACTIVE_CHANGES`,
   `OPENSPEC_VERIFICATION` (`false` skips checkpoint verification), and
   `OPENSPEC_AGENT_HOST_TESTS` (`true` enables the agent's `run_verification`
   tool), and require the `OpenSpec lifecycle` check in branch protection.

`openspec-agent.md` is a gh-aw workflow compiled with gh-aw v0.89.21. Edit
only the Markdown source, recompile with `gh aw compile openspec-agent`, then
run `node .github/scripts/openspec-change-workflow-names.mjs` to apply
readable names to the generated jobs. Never hand-edit the lock file.

Agent Tasks on an existing pull request run in GitHub's review-comment
follow-up mode, which may stop when no comment mentions Copilot. The workflow's
prompt therefore opens with an explicit instruction that the problem statement
is the request. Keep that opening when changing the prompt.

The workflow uses `GITHUB_TOKEN`, which is limited to 1,000 API requests per
hour per repository. To stay within it, each run reads state from the head
commit, scans only pull requests closed in the last 24 hours for finalization,
and refreshes overview comments and labels only for changes the run touched.
Use `OPENSPEC_MAX_ACTIVE_CHANGES` if many changes run in parallel.

## Repository-specific verification and tooling

The `openspec-*` workflows, the `setup-openspec` action, and the
`openspec-*.mjs` scripts are generic: they know OpenSpec, not this repository's
solution. Everything specific to the repository lives in files the repository
provides:

| File | Contract |
| --- | --- |
| `.github/workflows/verification.yml` | Reusable workflow (`workflow_call`) with the inputs `change`, `sha` (checkpoint), and `baseline` (head before the operation started). It decides what is relevant, fails when verification fails, and uploads the artifact `openspec-verification-<change>` with `verification.txt` (agent feedback, most important lines first) and optionally `log.txt`. It gets a read-only token and no secrets. A repository without tests provides one that succeeds. |
| `.github/workflows/shared/repository-toolchain.md` | gh-aw shared component that `openspec-agent.md` always imports. It may add `network` domains, `pre-agent-steps`, and an mcp-script named `run_verification`. A repository without extra tooling keeps it with empty frontmatter. Optional imports (`path?`) are not supported by gh-aw v0.89.21. |
| `.github/actions/repository-toolchain/action.yml` | Composite action that installs the repository toolchain; the single place for its versions. |

This repository's verification runs the platform tests: it checks out the
trusted tooling from `main` and the checkpoint into `checkpoint/`, skips when
nothing under `src/platform/` changed since `baseline`, installs the toolchain
(.NET from the checkpoint's `src/platform/global.json`, plus Node.js), runs
`dotnet test src/platform/SocAlytics.Platform.slnx` with Testcontainers, and
writes `verification.txt` with `.github/scripts/dotnet-test-summary.mjs`: build
errors, then each failed test with its error message, the repository's own
stack frames, and test output.

Installations are shared through composite actions, the only place their
versions are pinned:

| Action | Installs | Used by |
| --- | --- | --- |
| `.github/actions/setup-openspec` (generic) | Node.js 24 and, unless `cli: false`, the pinned OpenSpec CLI | every `openspec-*` workflow and `copilot-setup-steps.yml` |
| `.github/actions/repository-toolchain` | .NET from `global.json` and Node.js 24 | `verification.yml`, `shared/repository-toolchain.md`, and `copilot-setup-steps.yml` |

In the agent job, the imported pre-agent steps run before the pull request
branch is checked out, so the toolchain comes from `main`; a change that bumps
the SDK takes effect for agents after it merges. `copilot-setup-steps.yml` is
generated by OpenSpec; re-apply the composite actions if `openspec update`
regenerates it.

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
gh aw validate .github/workflows/openspec-prepare.md
gh aw validate .github/workflows/openspec-agent.md
gh aw lint .github/workflows/openspec-prepare.lock.yml
gh aw lint .github/workflows/openspec-agent.lock.yml
node .github/scripts/openspec-change-workflow-names.mjs --check
```

`openspec-workflow.test.mjs` enforces that every job and step in
`openspec-orchestrator.yml` has a readable name and that the workflow keeps trusted
checkouts, least privilege, and the repository-wide lock. It also keeps the
`openspec-*` workflows and scripts free of repository specifics, requires
installations to go through the composite actions, and checks the
verification workflow's contract. Compile the
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

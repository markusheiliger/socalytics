# Spec Kit workflows

The `speckit-*` workflows take a Spec Kit feature folder under `specs/` from a twin issue to a merged implementation.
They run from the default branch and decide from the state on GitHub (issues, labels, pull requests, check runs),
so a run can be repeated or replaced safely.

## Overview

```mermaid
flowchart TD
    person(["Person"])
    specs[("specs/** on the default branch")]
    cron(["Hourly schedule"])

    prepare["<b>🤖 Spec Kit prepare</b><br/>Syncs one twin issue per spec folder,<br/>infers blocked-by dependencies,<br/>validates implement requests"]
    orchestrate["<b>⚙ Spec Kit orchestrate</b><br/>Schedules implementations,<br/>restarts broken chains,<br/>finalizes merged or closed pull requests"]
    commands["<b>⚙ Spec Kit commands</b><br/>/speckit comments and<br/>amendment feedback"]
    backlog["<b>🤖 Spec Kit backlog</b><br/>no spec left to request:<br/>authors the next specs like<br/>/speckit-gha-author --repeat"]

    subgraph chain["Implementation chain · one per spec"]
        spec["<b>⚙ Spec Kit implement decide</b><br/>a run without work: reports stops,<br/>starts the diagnosis, marks done"]
        kinds["<b>⚙ Spec Kit implement group · phase verification · repair · merge</b><br/>one unit per run: a sequential or parallel group (≤ 5 at once),<br/>the verification of a phase, a repair, or the merge"]
        prepare["<b>⚙ Spec Kit implement prepare</b><br/>merge the base branch → decide what comes next"]
        work["<b>🤖✍ Spec Kit implement task</b><br/>a task, the convergence, a repair, or a conflict resolution:<br/>gha Spec Kit workflow → verify → land"]
    end

    subgraph amend["Amendment and new spec loop · spec artifact defects and causes outside the spec"]
        diagnose["<b>🤖 Spec Kit diagnose</b> (agentic)<br/>diagnose | fix | revise"]
        fixspec["<b>✍ Spec Kit author</b><br/>decision step + Spec Kit workflow author | fix | revise"]
        analyze["<b>🤖 Spec Kit analyze</b> (agentic)<br/>read-only consistency check"]
    end

    implPr[["Implementation pull request<br/>speckit/*"]]
    amendPr[["Amendment pull request<br/>speckit-amend/*"]]
    fixPr[["Spec pull request<br/>speckit-spec/* → default branch,<br/>merged automatically when consistent"]]

    person -- "edits" --> specs
    specs -. "push" .-> prepare
    person -. "/speckit implement on the twin" .-> commands
    commands -- "implement requested" --> orchestrate
    prepare -. "completed" .-> orchestrate
    cron -.-> orchestrate
    implPr -. "closed, synchronize" .-> orchestrate

    orchestrate -- "creates branch, draft pull request" --> implPr
    orchestrate -- "starts or restarts" --> spec

    spec == "prepare the run" ==> prepare
    kinds == "prepare the run" ==> prepare
    spec -- "start the work's kind" --> kinds
    kinds -- "start the next run" --> kinds
    kinds -- "stop, wait, done" --> spec
    kinds == "once per task of the group, a repair" ==> work
    work -- "pushes tasks, convergence tasks, repairs, resolutions" --> implPr
    kinds -- "squash-merge or request review" --> implPr

    spec -- "stopped" --> diagnose
    diagnose -- "opens or reworks" --> amendPr
    diagnose -- "check amendment" --> analyze
    fixspec -- "opens or reworks" --> fixPr
    fixspec -- "check new spec" --> analyze
    analyze -- "findings: fix round, feedback: revise" --> diagnose
    analyze -- "findings: fix round, feedback: revise" --> fixspec
    analyze -- "clean: hand to a person" --> amendPr
    analyze -- "clean: hand to a person" --> fixPr

    person -. "/speckit comment" .-> implPr
    person -. "review, push, merge, close" .-> amendPr
    person -. "review, push, merge, close" .-> fixPr
    implPr -. "/speckit retry, sync, fix" .-> commands
    amendPr -. "feedback, push, close" .-> commands
    fixPr -. "feedback, push, close" .-> commands
    fixPr -. "merged: push to specs/**" .-> prepare
    commands -- "retry, sync, amendment merged" --> spec
    commands -- "revise" --> diagnose
    commands -- "fix, revise" --> fixspec
    commands -- "person pushed: check" --> analyze
    orchestrate -- "blockers implemented: retry" --> spec
    orchestrate -- "no spec left to request" --> backlog
    backlog -- "pushes new specs, dispatches Spec Kit prepare" --> specs
```

Edges: dotted arrows are GitHub events that trigger a workflow, solid arrows are workflow dispatches or effects on a
pull request, and thick arrows are reusable workflow calls (`workflow_call`) that run inside the caller's run.
Marks: 🤖 runs AI (Copilot CLI or an agentic workflow), ⚙ is script only, and ✍ receives command guidance (see
[Where AI runs and where guidance applies](#where-ai-runs-and-where-guidance-applies)).
Implementation pull requests stay draft while the chain is working, stopped for a decision, or waiting for a diagnosis.
They become ready only after final verification, immediately before an automatic merge or an intentional review handoff.

## Workflows

| Workflow | File | Triggered by | Does |
| --- | --- | --- | --- |
| Spec Kit prepare | [speckit-prepare.yml](speckit-prepare.yml) | Push to `specs/**`, stage label changes (restored when a person made them), manual | Keeps one twin issue per spec folder, infers dependencies between new twins, validates implement requests; a dispatched, applying run then dispatches `Spec Kit orchestrate`. |
| Spec Kit orchestrate | [speckit-orchestrate.yml](speckit-orchestrate.yml) | Prepare completed, twin closed or reopened, `speckit/*` pull request closed or pushed, dispatched by every automatic merge and every dispatched, applying prepare run, twice an hour as a fallback, manual | Starts implementation chains up to `SPECKIT_MAX_ACTIVE_SPECS` (default 3; blocked implementations keep their slots; requested twins that block running implementations start regardless and first, the one that unblocks the most waiting implementations, also through chains of blocked ones, first), restarts broken chains, finalizes merged implementations, settles amendments and spec pull requests authored by `/speckit author`, blocks running implementations whose twin has open blocked-by twins, and lifts the block and retries once they are implemented. Starts `Spec Kit backlog` once no spec is left to request with `/speckit implement`. Events caused by the workflow token (an automatic merge, the twin it closes) start no workflows except dispatches, so the explicit dispatches are the main path after a merge. |
| Spec Kit implement decide | [speckit-implement-decide.yml](speckit-implement-decide.yml) | Orchestrate, commands, the chain | The run without work: when the next work is none (a stop at an attempt limit, a failure, a running merge, a finished implementation) or cannot be told, it decides and acts on it (reports the stop, starts the diagnosis, marks the implementation done); work it finds after all is handed over to the workflow of its kind. |
| Spec Kit implement group | [speckit-implement-group.yml](speckit-implement-group.yml) | Orchestrate, commands, the chain | A sequential group (consecutive tasks without `[P]`, one at a time), a parallel group (consecutive `[P]` tasks under one heading, at most 5 at once, verified together only when a landing merged a sibling's edits to the same files), or the convergence or a conflict resolution as a group of one; then starts the next run. |
| Spec Kit implement verify | [speckit-implement-verify.yml](speckit-implement-verify.yml) | The chain | The full verification of a finished phase in a run of its own, in up to four shards side by side that a planning job names after their work (combined into the artifact `speckit-phase-verification`); a failure stops with a pending repair. |
| Spec Kit implement repair | [speckit-implement-repair.yml](speckit-implement-repair.yml) | The chain | A writer-approved repair of landed code and the reverification of its original failed scope, in up to four shards side by side like the phase verification (combined into the artifact `speckit-repair-verification`), with the changed environment actions self-tested once beside them. |
| Spec Kit implement merge | [speckit-implement-merge.yml](speckit-implement-merge.yml) | The chain | Archives the spec, merges the base branch once at a pinned commit and verifies that prospective merged tree in up to four shards side by side with a read-only token (each shard rebuilds the tree and must match it; combined into the artifact `speckit-merge-result`), self-tests changed environment actions once beside them, then squash-merges or hands to a person. |
| Spec Kit implement prepare | [speckit-implement-prepare.yml](speckit-implement-prepare.yml) | Every workflow of the chain (`workflow_call`) | Prepares a run: checks the spec issue, merges the base branch when the next work needs it, and decides what comes next; work of another kind than the calling workflow's is handed over before any attempt starts. |
| Spec Kit implement task | [speckit-implement-task.yml](speckit-implement-task.yml) | Implement group, repair (`workflow_call`) | One task, the convergence, a repair, or a conflict resolution: runs its gha Spec Kit workflow with a read-only token, packages and verifies the change, then validates, ticks, commits, pushes, and reports it through a write-isolated land job. |
| Spec Kit commands | [speckit-commands.yml](speckit-commands.yml) | Pull request comments, reviews, and events | Handles `/speckit` commands (`retry`, `sync`, `author`, and `implement` on twin issues, `help`) and feedback on amendments and authored spec pull requests; keeps Spec Kit pull requests out of stacks. |
| Spec Kit diagnose | [speckit-diagnose.md](speckit-diagnose.md) ([lock](speckit-diagnose.lock.yml)) | The implementation chain, analyze | Automatically explains why an implementation stopped and recommends one ready-to-post command that fits the category, chosen from the "Commands you can recommend" reference its evidence generates from `PR_COMMANDS`; for spec artifact defects opens and reworks an amendment pull request. |
| Spec Kit backlog | [speckit-backlog.yml](speckit-backlog.yml) | Orchestrate (no spec left to request), manual | Authors the next `SPECKIT_BACKLOG_SPECS` (default 5) logical specs like `/speckit-gha-author --repeat` in autopilot and publishes them straight to the default branch, then dispatches `Spec Kit prepare` for their twins (see [Spec backlog](#spec-backlog)). |
| Spec Kit author | [speckit-author.yml](speckit-author.yml) | Commands (`/speckit author`), analyze | One decision step (reuse, refuse, or a feature description), then the Spec Kit workflow `author` unattended to author an ordinary spec for a cause outside the stopped spec and propose it against the default branch (one cause, one spec); reworks it after findings or feedback from the stage they concern. A consistent spec pull request is merged automatically unless it changes `docs/architecture/` (with a **Design decisions** comment from its plan) or a person pushed to it. |
| Spec Kit analyze | [speckit-analyze.md](speckit-analyze.md) ([lock](speckit-analyze.lock.yml)) | Diagnose, author, commands | Runs a read-only `/speckit-analyze` of the amended spec folder or the new spec and decides between a correction round, a rework, or human review. |

The implementation jobs use typed command scripts (`speckit-implement.mjs`, `speckit-group.mjs`, `speckit-repair.mjs`,
`speckit-converge.mjs`, `speckit-resolve.mjs`, and `speckit-merge.mjs`). The input `kind` of Spec Kit implement task is a verb, `implement`
(a task), `converge`, `repair`, or `resolve`, and selects its script `speckit-<kind>.mjs`; attempt markers keep the
attempt kind `task`. Its other inputs (`issue`, `pull`, `folder`, `base`, `order`, and `entry`) mean the same for every
kind: `entry` is the work item as `decide` built it (`index`, `task`, `attempt`, `guidance`, and the fields of its kind,
such as `repair`, `defer_progress`, `required_sync`, and `sync_repair`). Attempt accounting,
result artifacts, verdicts, and landing stay shared
in `speckit-implement-work.mjs`. Common Actions
script plumbing—GitHub client construction, step summaries, and outputs—lives in `speckit-script-runtime.mjs`.
Git runners, optional-file readers, and link contexts remain local because their buffer, error, or missing-value
contracts differ.

## Units of work

- **Spec:** one feature folder under `specs/`, one twin issue, one `speckit/<folder>` branch, and one implementation
  pull request.
- **Phase:** a `##` heading of the spec's `tasks.md`.
- **Group:** the unit of one run of the chain, within one phase. A **parallel group** is the consecutive unticked `[P]`
  tasks under one heading (also a single one); its tasks run at the same time, at most 5 at once (`max-parallel: 5`),
  and each lands on its own. A **sequential group** is the consecutive unticked tasks without `[P]` up to the next
  `[P]` task or the end of the phase; its tasks run one after another (`max-parallel: 1`), and a task that does not land
  cancels the tasks after it. The size of a group is not capped (beyond GitHub's 256 jobs per matrix).
- **Task:** one checkbox line of `tasks.md`. A landing in a parallel group that has to merge a sibling's edits to the
  same files three-way names them in its commit message (`Combined with parallel changes to …`); only then are the
  group's changes verified together, with their affected tests. Measured on the implementation pull requests up to
  #179, a full verification after every multi-task group cost about three times the critical-path time that
  parallelism saved, and none of its failures came from the parallel tasks interacting; the phase verification catches
  the same failures.
- **Phase verification:** after the last group of a phase, the next run verifies the whole phase (all changes of the
  branch, mode `phase`) in a run of its own; a pass is recorded as a `verify` attempt of the phase (a repaired phase
  verification records it too), a failure stops with a pending repair. A phase whose tasks were all ticked before the
  chain ran is not verified here; the merge verifies everything.

Each run of the chain does one of these, in the workflow of its kind: a sequential or parallel group, the convergence
(`/speckit-converge`), or a conflict resolution (Spec Kit implement group), the verification of a finished phase (Spec
Kit implement verify), a guided repair (Spec Kit implement repair), or the merge (Spec Kit implement
merge). Every run first prepares itself with Spec Kit implement prepare; the run that starts the next one predicts its
work without changing anything and starts the workflow of its kind. When the state changed in the meantime (a person
pushed, the base branch moved), the run hands the work over to the right workflow before it starts any attempt, so no
attempt is charged to it. A run that finds no work (a stop, a wait, or a finished implementation) reports it itself. A failed verification of already-landed code records
durable repair state without unticking tasks. Diagnosis recommends `/speckit retry <guidance>`; after a writer approves
it, repair work lands through a write-isolated job and the failed scope must pass reverification before normal planning
continues. For pending repairs, `/speckit retry` without guidance reverifies without a repair
agent; with guidance it runs the repair
agent, and a successful no-change result reaches reverification without creating an empty commit. Agent failures and
invalid or out-of-scope patches remain failures. Every failed repair attempt stops for a person, and every restart
(`/speckit retry`, a merged amendment, or implemented blockers) gets a fresh repair attempt; the
[unchanged-restart guard](#retry) keeps restarts from looping. Every unresolved implementation stop starts a diagnosis;
while it runs, the stop comment links to the diagnosis workflow so a person can inspect its progress. A reported
diagnosis then waits for its recommended action instead of being started again, unless the automation continues by
itself (see [Automatic continuation](#automatic-continuation)).
A task's agent leaves `tasks.md` alone: after the verification passed, the land job checks exactly that task's box
(an agent's own correct tick is still accepted, any other change of `tasks.md` is refused).
A task whose work a push already did (a person's, or Copilot's for them, after the task's first attempt or at the branch
head) lands without a change of its agent: the land job only ticks it. Without such a push, an agent that changes
nothing may land only when verification of the unchanged branch passes on the current head, includes a test or build
check for the files the task names, leaves none of them uncovered, and a file only this task names either differs on
the implementation branch from its base or changed on the base branch since the implementation pull request was
opened (the work landed elsewhere and was merged in). A task with no such evidence (including a missing or failed
verification, a head mismatch, or files named by another task only) fails the attempt; a Markdown-only check does not
qualify. A task ticked without its agent's change is never ticked silently: the land job appends a provenance note to
its line in `tasks.md` (for example `— already satisfied by 8e71cbfd on main (verified; no change by this task)`),
and its comment and check run say the same, so the tick never claims work this spec did not do; agents cannot add
such a note themselves.
A task can also be correctly done without any change, which upstream Spec Kit allows: it only verifies (for example
"Run quickstart.md validation", or a test run a number of times), or it applies only under a condition that does not
hold ("only if T004 shows a platform defect, fix it"). Its agent then changes nothing and claims the outcome
(`verified` or `not-applicable`) in `completion.json` of the run folder; there are no task markers, the outcome is
inferred from the task's wording and the evidence. A separate read-only agent assesses the claim against the task
(`assessment.json`): it lists every obligation of the task and accepts only when none requires a change; for
`verified` it names exactly what the task requires to run (the `dotnet-test`, `pytest`, `npm-script`, or
`environment-verify` runner with its scope, filters, and number of consecutive passing runs) and lists anything else
(probes, load, manual steps) as unsupported, which keeps the task open; for `not-applicable` it names the condition and
cites repository lines (path, line range, and an excerpt that must appear there) that show it does not hold. The
workflow fails when the assessment rejects the claim, changes a file, or leaves an obligation unmet; ambiguity fails
closed. In the work job, the trusted runner (`speckit-completion.mjs verify`, step "Run the task's own verification",
time budget `SPECKIT_COMPLETION_BUDGET_MINUTES`, default 25) executes the assessed requirements on the unchanged
branch without reruns, and the environment verification runs only for an assessed `environment-verify` requirement,
in its mode over its files. The land job checks everything again without running agent-written code: the assessed
head is still the pull request head, the task text is unchanged, every citation still matches its content, and every
required run passed (a test that passed only on its rerun does not count). It then ticks the task with a note
(`— verified without changes by the assessed verification at 8e71cbfd (no change by this task)` or
`— not applicable: <condition> (assessed at 8e71cbfd; no change by this task)`), and its comment links the evidence.
When the branch moves before the push, the task is requeued and assessed again on the new head instead of carrying
the decision over. The diagnosis never amends such a task only to give it a file to write.
The verification reruns failing tests once: a .NET test project with at most 10 failed tests reruns only those, and
the Web UI browser tests run with `--retries=1`. A test that passes on the rerun lets the check pass but is reported as
**flaky** in the comment (marker `speckit-implement:flaky`); a test that fails twice fails the check. After each
scheduler run, a test that was flaky in `SPECKIT_FLAKY_THRESHOLD` (repository variable, default 3) implementation pull
requests within 14 days gets one `/speckit author` run, started on the latest open implementation pull request where
it was flaky (marker `speckit-implement:flaky-spec`) and without blocking it; the spec makes the test deterministic
and is implemented once someone signs it off with `/speckit implement` on its twin.
Reverification uses the original failure artifact's changed files together with repair changes, not just the diff since
the failed head. Missing or empty original scope falls back to full (`finalize`) verification. Downloaded evidence is
readable by the repair agent inside its workspace at the locally ignored `.speckit-repair-evidence/`; unavailable
artifacts do not weaken verification. The verification action must actually succeed, and the verified commit must still
be the pull request head, before the marker can resolve; verification-only work uses the decision's pinned head.
The comment commands that restart, synchronize, or fix a stopped implementation are described in
[Comment commands](#comment-commands). The chain keeps its branch current with the base branch by pulling: every run starts with
the job `sync` ("Merge the base branch"), which merges the base branch into the implementation branch through the
GitHub API when it is ahead and the next work is a phase that has not started (none of its tasks is ticked yet), the
convergence, or the merge (repository variable `SPECKIT_SYNC_BASE`, default `true`). Retries (`/speckit retry`),
merged amendments, retries after implemented blockers, and continuations of a started phase keep its base. On a conflict the
decide job of the same run starts the conflict resolution the merge uses (Copilot CLI edits the conflicted files and,
to fix clashes without a textual conflict such as two migrations with the same number, the implementation branch's
own files and new files; never protected paths or the environment actions; the full verification runs), and
the next run does the work on the merged branch. When the merged result can only pass with a change to a file that
only the base branch owns (for example a test the base branch added that does not know a setting this implementation
made required), the resolution may make the smallest such change, but never delete such a file or weaken a test. The
land job finds these files itself while it replays the resolution, lists them with their diff against the base branch
in the resolution comment (marker `speckit-implement:base-owned`), and the final merge then waits for a person's
review instead of merging automatically.
Conflicts under `specs/` never wait for a person and need no agent when the archive caused them: both the branch (which
archives its own spec before the merge) and the base branch (which archived other specs meanwhile) rewrite references
to archived specs, often on the same lines. Every base merge (the job `sync`, the conflict resolution, the merge's
archive step, and the land job's replay) first resolves them with the archive script (`resolveSpecConflicts` in
`speckit-archive.mjs`): outside the implementation's own spec folder it takes the base branch's version and applies
every archive to it; in the own folder it normalizes both sides and their merge base with every archive and merges them
three-way. After every such merge, also without conflicts, the script points references in all files to every archived
spec, for example in a spec the base branch added that links to the spec this branch archived. Only conflicts that
remain go to Copilot: code, and real edits of the own spec's artifacts that the base
branch changed too. Copilot may resolve the latter if it keeps every task and every checked box (the land job checks
`tasks.md`), and the final merge waits for a person's review of them. Outside the own spec folder an implementation may
change files under `specs/` only by reference rewrites: the merge verification refuses any other difference from the
base branch (`referenceOnlySpecChanges`). Conflicts under `.github/` and `.specify/` still need a person.
Before a phase or the convergence,
this is best effort: when GitHub refuses the merge, the conflicts touch protected paths, or the resolution fails
`MAX_SYNC_RESOLVE_ATTEMPTS` times, the implementation continues on its current base without stopping, that base commit
is recorded as skipped and not tried again, and the merge at the
end integrates it. Before the merge it is required, as the merge's own
conflict resolution. Nothing reacts to pushes to the base branch during a started phase. The workflow token may push these merges
even when the base branch changed workflow files, because they only bring in commits the repository already has. Job
names say what a job does, without step numbers; GitHub shows their order. The convergence starts with the `gha` extension's `before_converge` hook
`/speckit-gha-drift`, which checks `docs/architecture/` against the implementation; each stale, missing, or
contradicting statement becomes a convergence task, so the architecture is corrected before the merge. The hook comes
from the implementation branch, so a branch created before it existed gets it with the next merge of the base branch.

The merge first archives the spec to `specs/archive/<folder>` on the implementation branch (job `archive`),
so the archive lands with the implementation. Each workflow of the chain runs only its own jobs, one after another:
`Prepare the run` (Spec Kit implement prepare: `Merge the base branch`, `Decide what comes next`), the jobs of its kind
(for example `T013 / Implement T013`, `Verify the parallel tasks together`, `Report the group verification`), and
`Start the next run`. Prepare, orchestrate, the workflows of the chain, and author also have a `refuse` job that only
runs, and fails, when started from a branch other than the default branch.

The workflows that work on one spec (the workflows of the implementation chain, author, diagnose, and analyze) take
its spec twin as the input `issue` and check it first: the step "Check the spec issue" (`speckit-issue.mjs check`)
fails unless the number names an issue, not a pull request, labelled `speckit:spec`, and the jobs that write run only
when it passed. The chain checks it in `Merge the base branch` of Spec Kit implement prepare (and `decide` runs only
after it passed) and in the work job of Spec Kit implement task (its `land` job runs only after it passed). The scripts keep calling it the twin
(`SPECKIT_TWIN`).

## Comment commands

People with write access steer a stopped or running implementation by commenting on its implementation pull request
(`speckit/<folder>`); the command must be the first word of the comment, and `Spec Kit commands` handles it. Amendment
pull requests (`speckit-amend/<folder>`) and spec pull requests authored by `/speckit author` (`speckit-spec/<folder>`) need no commands: merge one
to apply it, comment or review it (*Comment* or *Request changes*) to have it reworked, or close it to discard it.

`PR_COMMANDS` in `.github/scripts/speckit-implement-core.mjs` is the single source of the commands, their flags, and
the diagnosis categories they fit. The table below, the reply to `/speckit help`, and the command reference in every
diagnosis evidence file are generated from it, and a test fails when this table is stale; refresh it with
`node .github/scripts/speckit-commands.mjs docs --write`.

<!-- speckit-commands:start -->
<!-- Generated from PR_COMMANDS in .github/scripts/speckit-implement-core.mjs; refresh with `node .github/scripts/speckit-commands.mjs docs --write`. -->

| Command | Where | What it does | Flags |
| --- | --- | --- | --- |
| `/speckit retry [--force] [guidance]` | implementation pull request | restart what stopped with a fresh attempt count while preserving the current phase base; guidance goes into the next attempt's prompt. Refused after 2 restarts in a row that left the branch unchanged, unless `--force` is given | `--force`: restart even after 2 restarts in a row that left the branch unchanged |
| `/speckit sync [guidance]` | implementation pull request | require merging the current pull request base and successful verification before any work continues, even when automatic sync is disabled or was skipped; Copilot resolves conflicts. Pending repair scope and attempt limits are preserved | none |
| `/speckit author [guidance]` | implementation pull request | author a new spec for a cause outside this spec with the Spec Kit workflow `author`; it is checked and merged to the default branch automatically, and this implementation waits until that spec is signed off with `/speckit implement` on its twin and implemented | none |
| `/speckit implement` | spec twin issue | request the implementation of this spec: checks that it is `tasked` with all checklists checked, records who requested it, and starts it on GitHub (it waits for open prerequisites) | none |
| `/speckit help` | anywhere | list these commands | none |

<!-- speckit-commands:end -->

### Format and flags

Every comment command has the form `/speckit <command> [flags] [guidance]`. Flags start with `--`, directly follow the
command, and are declared per command in `PR_COMMANDS`; case and trailing punctuation (`--force;`) are tolerated, a
bare `--` ends the flags (put it before guidance that itself starts with `--`), and a flag the command does not support
refuses the command instead of becoming guidance. An unknown command is answered with the help; a renamed one
(`specify` and `fix`, now `author`) is not run either, and the answer gives the command to post instead, with its
guidance (`RENAMED_COMMANDS` in `speckit-commands.mjs`).

### Guidance

Writer-supplied text after `/speckit retry`, `/speckit sync`, or `/speckit author` and their flags is called
**guidance**. Retry and sync guidance is stored in a bot-authored pull request marker so a replaced dispatch or
scheduled continuation cannot lose it. Copilot-bound guidance is whitespace-normalized, capped at 4,000 characters, and
passed as the `guidance` input of the Spec Kit workflow that runs every task in the first retried group, or the first
convergence, repair, or conflict resolution; the workflow adds it to its agent's prompt.
Later units do not inherit it, and it expires after successful work or a newer person-authored push. A clean merge and a verification-only repair restart invoke no agent, so they consume
no prompt guidance; clean sync verification keeps unconsumed guidance for the first continued work. Fix guidance goes
to the agent that authors the new spec. Guidance only steers an agent's prompt: it never changes which work
runs next, the checks, the attempt limits, or the verification. The reply to `retry` and `sync` says where the
guidance goes next, or that it cannot change the last stop (for example a stop before any agent ran).
[Where AI runs and where guidance applies](#where-ai-runs-and-where-guidance-applies) lists every place.

### Automatic continuation

When a diagnosis recommends `/speckit retry` (category `retry`, confidence medium or high) or `/speckit sync`
(category `outside`, the fix is already on the base branch), the diagnosis record step posts that command itself, with
the diagnosis guidance, as a comment that starts with the marker `speckit-implement:auto-continue` and says
"Continued automatically with … as the diagnosis recommends". It does so at most once after each action of a person
(a `/speckit` comment or a push), never when the same command already failed the same way, and never for `artifacts`,
`decision`, or `unknown`. A person is asked as before when it stops again. Set the repository variable
`SPECKIT_AUTO_CONTINUE` to `false` to always wait for a person.

### Retry

`/speckit retry` restarts a stopped implementation with a fresh attempt count and keeps the base of a started phase.
With a pending repair (see [Units of work](#units-of-work)), `retry` without guidance reverifies the failed scope
without a repair agent, and `retry` with guidance runs the repair agent first. Each restart comment records the
implementation branch head and the base branch head. While the implementation is stopped (`help wanted`),
`Spec Kit commands` refuses a `retry` after `MAX_UNCHANGED_RESTARTS` (2) restarts in a row on the same implementation
branch head, because it would most likely stop the same way again; the flag `--force`
(`/speckit retry --force [guidance]`) insists. The refusal points to a push, retry guidance based on the diagnosis, or
`/speckit author`.

### Author

`/speckit author [guidance]` handles a cause outside the stopped spec: another feature's code or tests on the base
branch, or the environment actions. The `Spec Kit author` workflow ([speckit-author.yml](speckit-author.yml)) is a
regular workflow without an agent of its own. Its job `author` (read-only token) collects the evidence, lets one
decision step (Copilot CLI) write `decision.json`: reuse an open or merged spec that already removes the cause, refuse
when no spec can remove it, or author one with a feature description. It then runs the deterministic Spec Kit
workflow `author` unattended (every recommended choice, `publish=commit`, the time stamp of the request as folder
prefix), whose agent steps call Copilot CLI through `speckit-copilot.mjs exec` (the level of `models.json` and the
Spec Kit deny-list, never `--yolo`). Its job `land` (write token, never runs agent-written code) accepts only changes
to the new spec folder and `docs/architecture/`, pushes `speckit-spec/<folder>`, and opens the draft pull request
against the default branch. Correction rounds (`fix`) and reworks after review (`revise`) run the same workflow on
the spec branch: the earliest artifact the findings or feedback name chooses the stage the run starts again at
(`spec.md`: clarify, plan files: plan, `tasks.md`: tasks), and `land` pushes the result to the pull request. The
**Design decisions** comment comes from the `## Architecture Updates` section of the spec's plan. Once the
analysis finds it consistent and its changed Markdown passes the Markdown check (the record job runs
`check-markdown.mjs` on it, and every problem is a finding for the correction rounds; amendments get the same
check), the pull request is merged automatically; it waits for review when it changes
`docs/architecture/` (a **Design decisions** comment explains why) or when a person pushed to it. On the default
branch it is an ordinary spec with its own twin, and the stopped spec's twin is blocked by it like by any dependency.
Once it is ready (`tasked`, checklists checked), the scheduler requests its implementation automatically on behalf of
the person who posted `/speckit author`, because it exists only to unblock the stopped implementation. The
stopped implementation is blocked until it is implemented and then retries by itself (see
[Pull requests that need attention](#pull-requests-that-need-attention)). When the fix is already on the base branch,
use `sync` instead.

Authored specs outlive their originating implementation pull request. If it has already merged, the scheduler
continues maintaining each open authored spec, using the bot's introductory comment and the reciprocal creation
event on the origin to resolve ownership. Feedback still starts a rework, and independent analysis and correction
rounds still apply. A neutral check discarded by the former open-origin requirement is restarted automatically;
it is not treated as a passing analysis. Non-blocking flaky-test specs never reopen, retry, or add a dependency to
the merged origin. These recovery changes must be on the default branch, which supplies the trusted tooling.

### Implement

`/speckit implement` is posted on a spec twin issue, not on a pull request, and is the only way to request an
implementation: every non-archived twin ends with a code block holding it, ready to copy. The command accepts a
person with write access, an open twin whose spec is on the default branch at stage `tasked`, and fully checked
checklists. It posts a record comment (who requested it and when, and its open blockers), the GitHub bot sets
`speckit:stage:implement`, and `Spec Kit orchestrate` starts the implementation once its blockers are closed.
`/speckit-gha-request` and `/speckit-implement` post the same comment with your token. Stage labels are set only by
the GitHub bot; a label a person adds or removes is restored by `Spec Kit prepare`, which recommends the command.

### Required synchronization

`/speckit sync [guidance]` explicitly requires synchronization before the implementation continues. It uses the
implementation pull request's actual base branch, not an assumed `main`, and is independent of `SPECKIT_SYNC_BASE`
and earlier best-effort skip markers. The request is durable across replacement dispatches and subsequent chain runs.
Unlike best-effort automatic synchronization, API refusals, protected conflicts, exhausted resolution attempts, and verification
failures stop for attention instead of continuing on an old base.
Command acceptance and branch publication share a per-implementation-PR concurrency queue, including commands
posted on an amendment PR after a read-only target lookup. Landing and merge paths also recheck the barrier at
publication, so a request cannot race an already-running publisher into bypassing synchronization.

The existing conflict-resolution workflow verifies the synchronized tree, invokes Copilot only when needed, and
retains its file restrictions and read-only agent/separate write-capable landing jobs. Clean merges and an
already-current branch also require verification. Before releasing the barrier, the controller checks the current
base again; if it moved, synchronization remains pending. Checked tasks and pending repair evidence are retained;
the original failed scope must still pass, and synchronization does not erase repair attempt limits.
Successful full verification can resolve a pending repair, including one whose repair attempts are exhausted,
when the original failed scope is covered; historical failures remain recorded. Full `finalize` verification
also covers task/group/phase repairs whose original artifact expired. Merged environment-action repairs still
require explicit scope evidence and their self-tests, which ordinary `finalize` verification does not run. The
self-test of a changed environment verification action (in the merge, a repair reverification, and a conflict
resolution) passes `environment-tests-only`, so it runs only that action's own tests and fixtures from the workspace
instead of repeating the component checks the full verification already runs (an action version without that input
ignores it and runs in full); its outcome still decides. In the merge and the repair reverification it runs once in a
job of its own beside the verification shards.

Use `retry` for unchanged work and `sync` when
receiving base-branch changes is a prerequisite. Diagnosis categorizes a fix already on the base as `outside` and
recommends `sync`; inconsistent category/command reports are rejected at publication, not rewritten into retries.
A diagnosis never recommends a command that already failed the same way: when the last `/speckit retry` or
`/speckit sync` left the implementation branch at the same head and the stop after it has the same reason as the stop
before it (`repeatedFailure`), the evidence tells the agent, and a recommendation of that command is moved to the other
options at publication, with `/speckit author` as the fallback.

Architecture impact: none. This changes repository automation, not the product design in `docs/architecture/`.

## Spec backlog

`Spec Kit orchestrate` keeps a spec ready to request. After each run it counts the specs a person can request with
`/speckit implement`: folders under `specs/` on the default branch at the computed stage `tasked` with every checklist
item checked, whose twin is open and not requested yet (or not created yet). When there is none, it starts
`Spec Kit backlog` ([speckit-backlog.yml](speckit-backlog.yml)) with `count` set to the repository variable
`SPECKIT_BACKLOG_SPECS` (default 5, at most 10; `0` turns the refill off). Only one refill runs at a time, and after a
refill that did not succeed, the next one starts a day later; start `Spec Kit backlog` by hand to refill sooner.

The refill does what `/speckit-gha-author --repeat <count>` does in autopilot: its read-only job `author` runs the
Spec Kit workflow `author` `count` times one after another, each without a feature, unattended (every recommended
choice), and committing its spec, so each run proposes the next logical spec from `docs/architecture/`, the existing
specs, and the spec twins, including the specs committed by the runs before it. It stops at the first run that fails
and before a run that would not fit into the job's time budget; the specs committed so far are kept. Its job `land`
(write token, never runs agent-written code) applies the commits on the latest default branch, checks that they only
add new spec folders (with `spec.md` and `tasks.md`) and change `docs/architecture/`, pushes them straight to the
default branch without a pull request, and dispatches `Spec Kit prepare`, which creates their twins and infers their
dependencies. Implementation still starts only when someone comments `/speckit implement` on a twin. The direct push
needs a default branch that lets the workflow token push; a refused push fails the run with the reason in its summary.

## Where AI runs and where guidance applies

Most of the automation is script code. AI runs only in the places below, each in a read-only job whose output a
separate job validates before anything is pushed. Model levels come from
[models.json](../actions/speckit-copilot/models.json) through the `speckit-copilot` action, or through the
`speckit-workflow` action for the agent steps of the gha Spec Kit workflows; the agentic workflows use the `copilot`
engine of GitHub Agentic Workflows.

<!-- speckit-ai:start -->
| Flow branch | Workflow | AI | Command guidance reaches it |
| --- | --- | --- | --- |
| Dependency inference for new twins | Spec Kit prepare | Copilot CLI, level `light` | never |
| Scheduling, chains, the decide and `sync` jobs | Spec Kit orchestrate, implement decide, implement prepare | none (script) | never |
| One task | Spec Kit implement task | Copilot CLI, level `standard`, in the Spec Kit workflow `implement-task` | `retry`: every task of the first group after the restart |
| Guided repair of landed code | Spec Kit implement task | Copilot CLI, level `strong`, in the Spec Kit workflow `repair` | `retry` with a pending repair: the repair agent (without guidance only reverification runs) |
| Convergence | Spec Kit implement task | Copilot CLI, level `strong`, in the Spec Kit workflow `converge` | `retry`: when the convergence is the first work after the restart |
| Conflict resolution | Spec Kit implement task | Copilot CLI, level `standard`, in the Spec Kit workflow `resolve`, only for conflicts left after the archive script | `retry` or `sync`: when it is the first work; a clean merge passes `sync` guidance on to the next work |
| Group and phase verification, repair reverification, archive, merge verification, squash merge (the phase, merge, and repair verifications run in shards of at most 90 minutes each; a conflict resolution's work job, which runs the agent and a full `finalize` verification together, has a 150-minute limit) | Spec Kit implement group, verify, repair, merge | none (script) | never |
| Comment commands | Spec Kit commands | none (script) | stores the guidance in a marker for the work above |
| Diagnosis, amendment correction and rework | Spec Kit diagnose | agentic (`copilot` engine) | never (it reads recent comments as context; rework rounds use analysis findings and review feedback) |
| New spec authoring (`/speckit author`), correction and rework | Spec Kit author | Copilot CLI, level `standard`: one decision step, then the agent steps of the Spec Kit workflow `author` | `author`: the decision step (the guidance shapes the feature description) |
| Spec backlog refill | Spec Kit backlog | Copilot CLI, level `standard`, in the agent steps of the Spec Kit workflow `author` | never |
| Consistency check of amendments and authored specs | Spec Kit analyze | agentic (`copilot` engine), read-only | never |
<!-- speckit-ai:end -->

A test (`speckit-docs.test.mjs`) keeps the table in line with the workflows: every workflow that runs the
`speckit-copilot` or `speckit-workflow` action or is agentic has a row with its level or engine.

## Pull requests that need attention

The automation's goal is to get pull requests merged by giving people the best guidance to reach that goal. Every
handoff to a person prominently recommends exactly one concrete next action, explains why it is the best way forward
and what it triggers, and identifies the target pull request. A comment action includes ready-to-paste text or a
`/speckit` command with actual guidance, never placeholders. A completed pull request explicitly recommends review
and merge. Alternatives stay collapsed. Correctable findings trigger automatic correction rounds, not just a report;
while automation is running, no action is needed yet. This goal never bypasses required verification or makes a
failed or missing analysis a reason to merge.

When the remedy is a change only a push to the implementation branch can make (another spec's folder, a protected
path, or the change a person's decision needs), a `decision` diagnosis recommends a ready-to-paste `@copilot` prompt
instead of describing the change: posted as a comment, it has GitHub Copilot's cloud agent (enabled for the
repository) push the change to the pull request's branch, and that push continues the implementation by itself like
any push that is not by `github-actions[bot]`. The prompt names the files, the exact change, and how to check it; the
comment adds a fixed scope (only this change, no task ticks, no other pull request). The prompt only removes what
stops the automation, never the spec's own work: a prompt that names a task, `tasks.md`, or asks to implement tasks
is rejected, because the Spec Kit implementation implements, verifies, and ticks every task itself. A `/speckit retry` that is refused
after unchanged restarts recommends the latest diagnosis' `@copilot` request when it has one.

Posting such a request is a handoff: when a person with write access comments `@copilot …` (the mention first) on an
implementation pull request that waits for a person (`help wanted`, not `blocked`), job "Hand a stopped implementation
over to Copilot" of `Spec Kit commands` removes `help wanted` at once and comments that Copilot is working on it and
that its push continues the implementation by itself. If Copilot replies without having pushed, or has not pushed
within 60 minutes, `Spec Kit orchestrate` puts `help wanted` back with a comment that says so and recommends following
the latest diagnosis again. A push, a `/speckit retry`, or a `/speckit sync` after the handoff ends it.

Implementation, amendment, and authored spec pull requests carry GitHub's default label `help wanted` ("Extra attention is
needed") while they wait for a person, and lose it as soon as a person's action puts automation back to work or the
pull request is merged or closed. The label is a convenience; the comments and check runs stay authoritative.

Every label comes with a comment that explains it. `help wanted` is only added together with a comment that recommends
exactly one next step (a diagnosis, an outcome to decide on, or a review); a stop whose diagnosis is still running
waits without it, so the label never appears before the recommendation does.

When an amendment passes its consistency check (or a spec pull request authored by `/speckit author` is held for review) and correction rounds are finished, its description,
review handoff comment, and notification on the stopped implementation explicitly recommend **Review and merge** the
pull request. They explain why and what merging triggers: an amendment resumes the implementation; a new spec is added
to the default branch, where it waits for `/speckit implement` on its twin, and the stopped implementation resumes once
it is implemented. A failed or missing analysis never gets this merge recommendation.
Failed checks, failed reworks, and exhausted correction rounds recommend a ready-to-paste feedback comment that
restarts rework and analysis. Starting new work replaces the previous recommendation with an explicit wait; a failed
or abandoned run replaces that wait with the recovery action in both the description and its comment.

An implementation that waits for another spec carries the separate custom `blocked` label, with `help wanted` only while a blocker waits for its `/speckit implement` request.
It waits whenever its twin has open blocked-by twins, however the dependency came about (`/speckit author`,
dependency inference, or a person), and while a spec that `/speckit author` authored is still being written, checked,
or reviewed. GitHub has no default label for an on-hold state. The label is only added together with a comment that
starts with **Blocked**: it names what the implementation waits for and sorts every blocker into what it needs: a spec
twin that is not requested yet (**Spec twins to request first**, with the ready-to-paste `/speckit implement` as the
recommended next step for all of them), an open implementation pull request to merge first (**Implementation pull
requests to finish first**), or a requested twin whose implementation has not started yet (**Requested, starting by
itself**; nothing to do). It is posted again when a blocker is requested or its implementation pull request opens, and
says how the implementation continues (marker `speckit-implement:blocked`); when the
scheduler reconciles the label on an implementation whose block is not explained yet, it posts the explanation once.
A blocked implementation carries `help wanted` as well exactly while a blocker is a spec twin that is not requested for
implementation: nothing starts that blocker by itself, and the **Blocked** comment recommends `/speckit implement` on it.
Once every blocker is requested (the explanation posted again then says that nothing is needed), `help wanted` is removed
and only `blocked` stays. The scheduler does not continue a blocked implementation's chain, and a running chain stops
before its next unit (no retry, no diagnosis) while it waits. The `blocked` label (and a `help wanted` that came with it)
is removed when the last blocker is
implemented (the retry comment says the block is lifted and the implementation continues with a fresh attempt count),
when a blocker or the authored spec pull request is closed without being implemented (that comment lists the next
steps with `help wanted`), or when the implementation pull request closes.

The custom label `roadblock` (`ROADBLOCK_LABEL`) marks the implementations to process first: those that are not
blocked themselves and that the most other implementations wait for, directly or through a chain of blocked
implementations (`waitingSets`), at most `SPECKIT_ROADBLOCK_PULLS` (repository variable, default 3; `0` turns it
off), ties by the lower twin number. Every scheduler run moves it with the queue: it is added together with a comment
that names the waiting implementations (marker `speckit-implement:roadblock`) and removed, without a comment, as
soon as others unblock more, the pull request is blocked itself, or nothing waits for it any more. The same order
decides which requested prerequisite starts first.

| Pull request | Labeled when | Unlabeled when |
| --- | --- | --- |
| Implementation | It stopped, a diagnosis reported without an amendment, an amendment or authored spec was discarded or misdirected, a spec could not be authored or was not implemented, or it is held for review. | An automatic diagnosis, `/speckit author`, `/speckit retry`, or a person's push gets it going again, an amendment was merged, it is blocked by another spec, or it was merged or closed. |
| Amendment or authored spec | It is presented for review, or a check or rework could not start or did not finish. | A rework starts after feedback, a check starts after a push, or it was merged, closed, or superseded. |

Every run names its spec and what it works on. A run of the implementation chain is named `Spec Kit implement spec #<twin> ·
<spec title> · <what the run works on>`, for example `Spec Kit implement spec #35 · Recording Lineage and Upload ·
Phase 3: User Story 1 · T013 to T016 one after another`, `… · T017, T018 in parallel`, `… · Verify Phase 3: User Story
1`, `… · Convergence`, or `… · Stop at the attempt limit of T013`: the run that starts it decides what comes next
without changing anything (`predictNextRun`), starts the workflow of its kind, and passes it as the input `subject`;
the run decides again when it starts, and its job summary says what it actually does. The workflow run titles of Spec Kit diagnose, analyze, and
author follow the same pattern: `Spec Kit <workflow> spec #<twin> · <why the run started>`, for example `Spec Kit author
spec #66 · Write a new spec that unblocks the implementation`.

## Spec Kit workflows in the jobs

The agents of the implementation do not get prompts from these workflows: the read-only work job of Spec Kit implement
unit runs the Spec Kit workflow of the gha extension that owns its work, through the composite action
[speckit-workflow](../actions/speckit-workflow/action.yml): `implement-task` (a task), `converge` (the
convergence), `repair` (a guided repair), and `resolve` (a conflict resolution, after the integrate step merged the
base branch). The GitHub workflows own the flow and the state of issues and pull requests (what runs next,
attempts, check runs, comments, commits, ticks); the Spec Kit workflows own the work on the level of a Spec Kit
command: the prompt, the agent step, and the check of the change with the land jobs' validators (see "Workflow Design"
in the [gha extension README](../../.specify/extension-src/gha/README.md)). The jobs pass only inputs: the folder, the
task, the guidance, and for a repair its tasks, scope, and evidence; the action turns the folder into
`SPECIFY_FEATURE_DIRECTORY` for the Spec Kit commands, and the job sets up Node.js and Python before the solution's
environment.

- **Unattended, inside the job:** the action runs `specify workflow run` with `mode=unattended` and an empty standard
  input, within the job's time limit (`timeout`, default 60 minutes). A run never outlives its job and is never
  resumed; a retry is a new run. A run that paused stopped at a gate, which is reported as a defect of the workflow.
- **Trusted tooling:** the definition (`.specify/workflows/<id>/workflow.yml`) and its helper come from the tooling
  checkout the action is loaded from (input `tooling`), never from the implementation branch. Every job of one run
  checks the tooling out at the same commit (`github.sha`), so the work job and the land job apply the same rules.
- **Same contract with the package step:** the action collects the run into the result folder: `agent-exit.txt` (0
  completed, 124 time limit, 1 otherwise), `agent.jsonl` (the agent's output, through `SPECKIT_AGENT_LOG` of
  `speckit-copilot.mjs exec`), `workflow.json` (the outcome and the workflow's own message, which becomes the
  attempt's reason), and `workflow-run/` (the run folder, for the diagnosis). It removes the run folder from the
  workspace before the package step stages the change. The package, verify, and land steps validate the change again
  and stay as they are.

The logic lives in [speckit-workflow-job.mjs](../scripts/speckit-workflow-job.mjs); a test checks that every
`speckit-workflow` step names a workflow the tooling has and passes only inputs that workflow declares. The server's
Spec Kit author job runs the Spec Kit workflow `author` the same way (from the tooling checkout, unattended).

## Copilot CLI and model levels

Every Copilot CLI run of these workflows goes through the composite action
[speckit-copilot](../actions/speckit-copilot/action.yml), or through the Spec Kit workflows the composite action
[speckit-workflow](../actions/speckit-workflow/action.yml) runs (see
[Spec Kit workflows in the jobs](#spec-kit-workflows-in-the-jobs)), loaded from the trusted default-branch checkout, so
an agent cannot change its own model, flags, or deny lists. The agentic workflows (diagnose, analyze) use gh-aw's own engine
settings instead. The Spec Kit workflow `author`, which Spec Kit author runs, calls Copilot CLI itself for its agent
steps; on GitHub its executable is `speckit-copilot.mjs exec` (`SPECKIT_INTEGRATION_COPILOT_EXECUTABLE`), which applies
the level in `SPECKIT_LEVEL` and the same arguments and deny lists as the action, and drops the `--yolo` and model the
workflow engine passes.

Callers choose a strength level, not a model. The levels and their models live in
[models.json](../actions/speckit-copilot/models.json) next to the action, the only place to change models; a new level
is one more entry there:

```json
{ "levels": { "light": { "model": "claude-haiku-5.5", "effort": "medium" } } }
```

| Level | Model | Reasoning effort | Used by |
| --- | --- | --- | --- |
| `light` | `claude-haiku-5.5` | `medium` | Prepare: dependency inference |
| `standard` | `claude-sonnet-5.5` | `high` | Implement task: tasks and conflict resolution |
| `strong` | `claude-opus-5.5` | `high` | Implement task: convergence and repair |

- The action's `model` and `effort` inputs override the level for a rare exception.
- An empty value or `auto` lets Copilot CLI pick its default model.
- When the CLI rejects the chosen model or effort before the agent starts, the action logs a warning and retries once
  with the default model; its outputs `model` and `fell-back` show what ran.
- Mode `inference` gives the model no tools. Mode `agent` allows all tools except `git push`, `git commit`, `gh`,
  `curl`, `wget`, and the caller's `deny-tools`.
- The action pins the Copilot CLI version (`cli-version`).
- Stronger models use the AI-credit budget (`SPECKIT_TASK_AI_CREDITS`, default 1000 per run) faster; raise it when a
  level moves to a stronger model.

The logic (level resolution, arguments, rejection detection) lives in
[speckit-copilot.mjs](../scripts/speckit-copilot.mjs) and is covered by its tests, including a check that
`models.json` is valid and that every `level` used in a workflow exists in it.

## Dependency Download Caches

[Dependency download caches](dependency-cache.yml) populates NuGet and npm downloads from a credential-free checkout
of the default branch. It runs on dependency/tooling changes there, or manually on that branch. Non-default-branch
runs are skipped; it has no input for another checkout ref. It never invokes an agent, builds, or tests. npm
population disables lifecycle scripts; NuGet restore evaluates only the trusted default branch's projects.

The shared [environment setup action](../actions/environment-setup/action.yml) uses explicit **restore-only** cache
actions before installing tools. Implementation and verification jobs never save their modified caches. Restoring
does not execute workspace restore targets or npm scripts. Existing tool installs, `dotnet restore`, `npm ci`,
builds, and tests still run normally. Missing or unavailable caches fall back to ordinary downloads; a hit is not
evidence of a successful build or test.

Keys separate ecosystem, runner OS/architecture, actual SDK or Node/npm versions, and dependency contents. NuGet
inputs include both solutions' central package lists, project files, props, targets, solutions, SDK pins, package
lockfiles when present, tool manifests, and feed configuration. npm inputs include package declarations, lockfiles,
and npm configuration. Tool versions come from the trusted setup and Copilot actions. Keys contain neither a
commit SHA nor an absolute checkout path. There are no broad fallback keys. Bump the `socalytics-downloads-v1`
schema in [cache-keys.mjs](../actions/environment-setup/cache-keys.mjs) when changing cache provenance or format.

Only `~/.nuget/packages` and npm's `~/.npm/_cacache` are saved: no credentials/configuration, npm logs,
`node_modules`, build outputs, test results, databases, or agent state. Configuration contents influence hashes but
are not stored in the caches. A task that changes dependencies may miss the trusted cache until its changes land;
its normal restore still resolves the new dependencies. Separate tasks with unchanged dependency inputs reuse the
same downloads.

### Measurements And Rollout

Before changing the actions, the logs of
[run 38701098384, job 116309370584](https://github.com/markusheiliger/socalytics/actions/runs/38701098384/job/116309370584)
provided this uncached sample on 2026-10-15:

| Operation | Observed duration |
| --- | --- |
| Environment setup (GitHub step timestamps) | 48 s |
| Markdown tool install | 5.6 s |
| Kiota install | 2.0 s |
| Chromium and system dependency install | 19.4 s |
| Platform restore | 6.6 s |
| Platform build | 22.6 s |
| Sequential platform test commands, including startup | 260.2 s |

This conflict-resolution sample had pre-existing test failures and stopped before Manager/Web checks. It is an
initial baseline, **not** a successful acceptance run, a complete phase measurement, or evidence of cache savings.

Every verification now writes separate command durations and exit codes to its log and job summary. GitHub's step
timings distinguish SDK setup, cache restores, and tool installation. Setup summaries report exact cache hits and
uncompressed restored bytes; the population job also queries GitHub for compressed stored sizes. Population time
is reported separately from consumer time.

After this tooling reaches the default branch:

1. Run **Dependency download caches** there to populate the caches, then run it again to verify exact hits and stored
   sizes. Dependency changes also trigger population automatically.
2. Compare successful cold and warm implementation/verification runs with equivalent dependency inputs, runner
   images, verification modes, and test scope. Record setup, restore, build, and test durations separately.
3. Measure the phase's elapsed wall-clock time from planning through phase verification, including scheduling and
   commit jobs; do not add parallel task durations together. Separate Copilot execution from cache-related gains.
4. Track exact hit rates and the sizes/evictions under the repository's Actions cache settings. Cache archives and
   restore overhead can outweigh short restores; retain this scope only when repeated runs show net benefit.
5. To benchmark Chromium independently, dispatch the workflow with **benchmark-chromium** enabled. Two fresh
   runners compare download plus system dependencies with cache restore plus fresh system dependencies. Playwright
   tool installation is outside both timed intervals. A separate always-run cleanup job deletes only that run's
   isolated benchmark cache. If cancellation interrupts cleanup, remove its `socalytics-chromium-benchmark-` entry
   from Actions caches manually.

Chromium caching is **not enabled** for implementation jobs. Only adopt it after repeated benchmarks demonstrate
net savings. No speedup or cold/warm phase comparison is claimed before those default-branch runs complete.

## Editing

The agentic workflows are authored in their `.md` file. Recompile them with `gh aw compile` and never hand-edit the
`.lock.yml` files. Run the workflow tooling tests with `node --test .github/scripts/*.test.mjs`.

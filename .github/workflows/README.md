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

    prepare["<b>Spec Kit prepare</b><br/>Syncs one twin issue per spec folder,<br/>infers blocked-by dependencies,<br/>validates implement requests"]
    orchestrate["<b>Spec Kit orchestrate</b><br/>Schedules implementations,<br/>restarts broken chains,<br/>finalizes merged or closed pull requests"]
    commands["<b>Spec Kit commands</b><br/>/speckit comments and<br/>amendment feedback"]

    subgraph chain["Implementation chain · one per spec"]
        spec["<b>Spec Kit implement spec</b><br/>decide → phase | convergence or conflict resolution | archive, verify, merge → continue"]
        phase["<b>Spec Kit implement phase</b><br/>plan → tasks one at a time<br/>(consecutive [P] tasks in parallel) → verify"]
        task["<b>Spec Kit implement task</b><br/>work: one agent per task → commit and push"]
    end

    subgraph amend["Amendment and fix spec loop · spec artifact defects and causes outside the spec"]
        diagnose["<b>Spec Kit diagnose</b> (agentic)<br/>diagnose | fix | revise"]
        fixspec["<b>Spec Kit fix</b> (agentic)<br/>author | fix | revise"]
        analyze["<b>Spec Kit analyze</b> (agentic)<br/>read-only consistency check"]
    end

    implPr[["Implementation pull request<br/>speckit/*"]]
    amendPr[["Amendment pull request<br/>speckit-amend/*"]]
    fixPr[["Fix spec pull request<br/>speckit-fix/* → default branch"]]

    person -- "edits" --> specs
    specs -. "push" .-> prepare
    person -. "labels twin speckit:stage:implement" .-> prepare
    prepare -. "completed" .-> orchestrate
    cron -.-> orchestrate
    implPr -. "closed, synchronize" .-> orchestrate

    orchestrate -- "creates branch, draft pull request" --> implPr
    orchestrate -- "starts or restarts" --> spec

    spec == "phase" ==> phase
    phase == "once per task or [P] tasks" ==> task
    spec == "convergence, conflict resolution" ==> task
    spec -- "continue" --> spec
    task -- "pushes finished tasks" --> implPr
    spec -- "merge: squash-merge or request review" --> implPr

    spec -- "stopped" --> diagnose
    diagnose -- "opens or reworks" --> amendPr
    diagnose -- "check amendment" --> analyze
    fixspec -- "opens or reworks" --> fixPr
    fixspec -- "check fix spec" --> analyze
    analyze -- "findings: fix round, feedback: revise" --> diagnose
    analyze -- "findings: fix round, feedback: revise" --> fixspec
    analyze -- "clean: hand to a person" --> amendPr
    analyze -- "clean: hand to a person" --> fixPr

    person -. "/speckit comment" .-> implPr
    person -. "review, push, merge, close" .-> amendPr
    person -. "review, push, merge, close" .-> fixPr
    implPr -. "/speckit diagnose, retry, resume, fix" .-> commands
    amendPr -. "feedback, push, close" .-> commands
    fixPr -. "feedback, push, close" .-> commands
    fixPr -. "merged: push to specs/**" .-> prepare
    commands -- "retry, resume, amendment merged" --> spec
    commands -- "diagnose, revise" --> diagnose
    commands -- "fix, revise" --> fixspec
    commands -- "person pushed: check" --> analyze
    orchestrate -- "fix spec landed: resume" --> spec
```

Edges: dotted arrows are GitHub events that trigger a workflow, solid arrows are workflow dispatches or effects on a
pull request, and thick arrows are reusable workflow calls (`workflow_call`) that run inside the caller's run.

## Workflows

| Workflow | File | Triggered by | Does |
| --- | --- | --- | --- |
| Spec Kit prepare | [speckit-prepare.yml](speckit-prepare.yml) | Push to `specs/**`, `speckit:stage:implement` label, manual | Keeps one twin issue per spec folder, infers dependencies between new twins, validates implement requests. |
| Spec Kit orchestrate | [speckit-orchestrate.yml](speckit-orchestrate.yml) | Prepare completed, twin closed or reopened, `speckit/*` pull request closed or pushed, hourly, manual | Starts implementation chains up to `SPECKIT_MAX_ACTIVE_SPECS` (default 3; fix specs start regardless), restarts broken chains, finalizes merged implementations, settles amendments and fix specs, and resumes an implementation once its fix spec has landed. |
| Spec Kit implement spec | [speckit-implement-spec.yml](speckit-implement-spec.yml) | Orchestrate, itself, commands | One per run: a phase, the convergence, a conflict resolution, or the merge; then dispatches its next run. |
| Spec Kit implement phase | [speckit-implement-phase.yml](speckit-implement-phase.yml) | Implement spec (`workflow_call`) | Plans the order of one phase's open tasks and runs them one at a time, consecutive `[P]` tasks in parallel; verifies the phase after its last task. |
| Spec Kit implement task | [speckit-implement-task.yml](speckit-implement-task.yml) | Implement phase, implement spec (`workflow_call`) | One task, or consecutive `[P]` tasks: a `work` job per task runs the agent and the verification, then a "Commit and push" job commits and pushes them in task order. Parallel edits to the same file are merged and verified together; only conflicting edits are redone. |
| Spec Kit commands | [speckit-commands.yml](speckit-commands.yml) | Pull request comments, reviews, and events | Handles `/speckit` commands (`diagnose`, `retry`, `resume`, `fix`, `help`) and amendment and fix spec feedback; keeps Spec Kit pull requests out of stacks. |
| Spec Kit diagnose | [speckit-diagnose.md](speckit-diagnose.md) ([lock](speckit-diagnose.lock.yml)) | Implement spec, commands, analyze | Explains why an implementation stopped and recommends one ready-to-post command that fits the category; for spec artifact defects opens and reworks an amendment pull request. |
| Spec Kit fix | [speckit-fix.md](speckit-fix.md) ([lock](speckit-fix.lock.yml)) | Commands (`/speckit fix`), analyze | Authors a fix spec for a cause outside the stopped spec and proposes it against the default branch; reworks it after findings or feedback. |
| Spec Kit analyze | [speckit-analyze.md](speckit-analyze.md) ([lock](speckit-analyze.lock.yml)) | Diagnose, fix, commands | Runs a read-only `/speckit-analyze` of the amended spec folder or the new fix spec and decides between a correction round, a rework, or human review. |

## Units of work

- **Spec:** one feature folder under `specs/`, one twin issue, one `speckit/<folder>` branch, and one implementation
  pull request.
- **Phase:** a `##` heading of the spec's `tasks.md`.
- **Task:** a checkbox line of `tasks.md`. The tasks of a phase run one after another in task order; consecutive
  `[P]` tasks under one heading run in parallel.

Each run of Spec Kit implement spec does one of these: a phase, the convergence (`/speckit-converge`), a conflict
resolution, or the merge. The chain keeps its branch current with the base branch by pulling: every run starts with
the job `sync` ("Merge the base branch"), which merges the base branch into the implementation branch through the
GitHub API when it is ahead and the next work is a phase that has not started (none of its tasks is ticked yet), the
convergence, the merge, or a resume (`/speckit resume`, a merged amendment, or a landed fix spec; repository variable
`SPECKIT_SYNC_BASE`, default `true`). Retries (`/speckit retry`) and continuations of a started phase keep its base. On a conflict the
decide job of the same run starts the conflict resolution the merge uses (Copilot CLI edits only the conflicted files,
the full verification runs), and the next run does the work on the merged branch. Before a phase or the convergence
this is best effort: when GitHub refuses the merge, the conflicts touch protected paths, or the resolution fails
`MAX_SYNC_RESOLVE_ATTEMPTS` times, the implementation continues on its current base without stopping, that base commit
is not tried again, and the merge at the end integrates it. Before the merge it is required, as the merge's own
conflict resolution. Nothing reacts to pushes to the base branch: a stopped implementation picks up a fix that landed
there when a person resumes it. The workflow token may push these merges
even when the base branch changed workflow files, because they only bring in commits the repository already has. Job
names say what a job does, without step numbers; GitHub shows their order. The convergence starts with the `gha` extension's `before_converge` hook
`/speckit-gha-drift`, which checks `docs/architecture/` against the implementation; each stale, missing, or
contradicting statement becomes a convergence task, so the architecture is corrected before the merge. The hook comes
from the implementation branch, so a branch created before it existed gets it with the next merge of the base branch. The merge first archives the spec to `specs/archive/<folder>` on the implementation branch
(job `merge-archive`), so the archive lands with the implementation. It runs only one of its `phase`, `converge-or-resolve`, or `merge-*` jobs, so the others
always show as skipped. Prepare, orchestrate, and implement spec also have a `refuse` job that only runs, and fails, when
started from a branch other than the default branch.

## Copilot CLI and model levels

Every Copilot CLI run of these workflows goes through the composite action
[speckit-copilot](../actions/speckit-copilot/action.yml), loaded from the trusted default-branch checkout, so an agent
cannot change its own model, flags, or deny lists. The agentic workflows (diagnose, analyze, fix) use gh-aw's own engine
settings instead.

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
| `strong` | `claude-opus-5.5` | `high` | Implement task: convergence |

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

## Editing

The agentic workflows are authored in their `.md` file. Recompile them with `gh aw compile` and never hand-edit the
`.lock.yml` files. Run the workflow tooling tests with `node --test .github/scripts/*.test.mjs`.

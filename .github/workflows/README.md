# Spec Kit workflows

The `speckit-*` workflows take a Spec Kit feature folder under `specs/` from a twin issue to a merged implementation.
They run from the default branch and decide from the state on GitHub (issues, labels, pull requests, check runs),
so a run can be repeated or replaced safely.

## Overview

```mermaid
flowchart TD
    person(["Person"])
    specs[("specs/** on main")]
    cron(["Hourly schedule"])

    prepare["<b>Spec Kit prepare</b><br/>Syncs one twin issue per spec folder,<br/>infers blocked-by dependencies,<br/>validates implement requests"]
    orchestrate["<b>Spec Kit orchestrate</b><br/>Schedules implementations,<br/>restarts broken chains,<br/>finalizes merged or closed pull requests"]
    commands["<b>Spec Kit commands</b><br/>/speckit comments and<br/>amendment feedback"]

    subgraph chain["Implementation chain · one per spec"]
        spec["<b>Spec Kit implement spec</b><br/>decide → phase | step | merge → continue"]
        phase["<b>Spec Kit implement phase</b><br/>plan → stages one at a time → verify"]
        task["<b>Spec Kit implement task</b><br/>work: one agent per task → land"]
    end

    subgraph amend["Amendment loop · spec artifact defects"]
        diagnose["<b>Spec Kit diagnose</b> (agentic)<br/>diagnose | fix | revise"]
        analyze["<b>Spec Kit analyze</b> (agentic)<br/>read-only consistency check"]
    end

    implPr[["Implementation pull request<br/>speckit/*"]]
    amendPr[["Amendment pull request<br/>speckit-amend/*"]]

    person -- "edits" --> specs
    specs -. "push" .-> prepare
    person -. "labels twin speckit:stage:implement" .-> prepare
    prepare -. "completed" .-> orchestrate
    cron -.-> orchestrate
    implPr -. "closed, synchronize" .-> orchestrate

    orchestrate -- "creates branch, draft pull request" --> implPr
    orchestrate -- "starts or restarts" --> spec

    spec == "phase" ==> phase
    phase == "once per stage" ==> task
    spec == "step: convergence, conflict resolution" ==> task
    spec -- "continue" --> spec
    task -- "pushes landed tasks" --> implPr
    spec -- "merge: squash-merge or request review" --> implPr

    spec -- "stopped" --> diagnose
    diagnose -- "opens or reworks" --> amendPr
    diagnose -- "check amendment" --> analyze
    analyze -- "findings: fix round, feedback: revise" --> diagnose
    analyze -- "clean: hand to a person" --> amendPr

    person -. "/speckit comment" .-> implPr
    person -. "review, push, merge, close" .-> amendPr
    implPr -. "/speckit diagnose, resume, sync" .-> commands
    amendPr -. "feedback, push, close" .-> commands
    commands -- "resume, sync, amendment merged" --> spec
    commands -- "diagnose, revise" --> diagnose
    commands -- "person pushed: check" --> analyze
```

Edges: dotted arrows are GitHub events that trigger a workflow, solid arrows are workflow dispatches or effects on a
pull request, and thick arrows are reusable workflow calls (`workflow_call`) that run inside the caller's run.

## Workflows

| Workflow | File | Triggered by | Does |
| --- | --- | --- | --- |
| Spec Kit prepare | [speckit-prepare.yml](speckit-prepare.yml) | Push to `specs/**`, `speckit:stage:implement` label, manual | Keeps one twin issue per spec folder, infers dependencies between new twins, validates implement requests. |
| Spec Kit orchestrate | [speckit-orchestrate.yml](speckit-orchestrate.yml) | Prepare completed, twin closed or reopened, `speckit/*` pull request closed or pushed, hourly, manual | Starts implementation chains up to `SPECKIT_MAX_ACTIVE_SPECS` (default 3), restarts broken chains, finalizes merged implementations, settles amendments. |
| Spec Kit implement spec | [speckit-implement-spec.yml](speckit-implement-spec.yml) | Orchestrate, itself, commands | One segment per run: a phase, a convergence or conflict resolution step, or the merge; then dispatches its next run. |
| Spec Kit implement phase | [speckit-implement-phase.yml](speckit-implement-phase.yml) | Implement spec (`workflow_call`) | Plans one phase of `tasks.md` into stages and runs them one at a time; verifies the phase after a final `[P]` group. |
| Spec Kit implement task | [speckit-implement-task.yml](speckit-implement-task.yml) | Implement phase, implement spec (`workflow_call`) | One stage: a `work` job per task runs the agent and the verification, then `land` commits and pushes in task order. |
| Spec Kit commands | [speckit-commands.yml](speckit-commands.yml) | Pull request comments, reviews, and events | Handles `/speckit` commands and amendment feedback; keeps Spec Kit pull requests out of stacks. |
| Spec Kit diagnose | [speckit-diagnose.md](speckit-diagnose.md) ([lock](speckit-diagnose.lock.yml)) | Implement spec, commands, analyze | Explains why an implementation stopped; for spec artifact defects opens and reworks an amendment pull request. |
| Spec Kit analyze | [speckit-analyze.md](speckit-analyze.md) ([lock](speckit-analyze.lock.yml)) | Diagnose, commands | Runs a read-only `/speckit-analyze` of the amended spec folder and decides between a correction round, a rework, or human review. |

## Units of work

- **Spec:** one feature folder under `specs/`, one twin issue, one `speckit/<folder>` branch, and one implementation
  pull request.
- **Phase:** a `##` heading of the spec's `tasks.md`.
- **Stage:** a single task, or a run of `[P]` tasks under one heading. Stages of a phase run one at a time; the tasks
  of a `[P]` stage run in parallel.
- **Segment:** what one run of Spec Kit implement spec does: a phase, a step, or the merge.

Each segment runs only one of the `phase`, `step`, or `merge-*` jobs of Spec Kit implement spec, so the others always
show as skipped. Prepare, orchestrate, and implement spec also have a `refuse` job that only runs, and fails, when
started from a branch other than the default branch.

## Editing

The agentic workflows are authored in their `.md` file. Recompile them with `gh aw compile` and never hand-edit the
`.lock.yml` files. Run the workflow tooling tests with `node --test .github/scripts/*.test.mjs`.

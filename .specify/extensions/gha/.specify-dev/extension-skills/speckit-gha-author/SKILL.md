---
name: speckit-gha-author
description: Author one spec (or, with --repeat <n>, n specs one after another) until
  it is ready to implement by running the deterministic Spec Kit workflow `author`
  and answering its gates in Chat; without input the workflow proposes the next logical
  spec from the architecture, and at the end it pushes the spec folder so the GitHub
  automation takes over.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Author Skill

## User Input

```text
$ARGUMENTS
```

The input is empty (the workflow offers to continue the active spec, or proposes the next logical spec from
`docs/architecture/`, the existing specs, the spec twin issues, and the code), a feature idea in free text, a spec
folder to continue at its next step, or a spec folder followed by a note: then pass the folder as `feature` and the note
as `feedback` (the run reworks the stage before the folder's next step).

A leading `--repeat <n>` (`n` a positive integer) authors `n` specs one after another: one run of the workflow per
spec, each proposing the next logical spec (see Repeat). It only goes with an otherwise empty input: refuse it
together with a feature idea or a spec folder, and say that `--repeat` authors proposed specs only. `--repeat 1` is
the same as no flag.

## Role

Requires the specify CLI, Node.js, and the Copilot CLI (on Windows the standalone `copilot.exe`).

You operate the Spec Kit workflow `author`, which the `gha` extension ships (source
`.specify/extension-src/gha/workflows/author/workflow.yml`). The workflow does the work: its
agent steps run the Spec Kit commands, and every decision between them comes from
`node .github/scripts/speckit-author-flow.mjs`. Your part is to start it, ask the person what its gates ask, resume it
with the answers, and report.

- Never run a Spec Kit skill yourself, never edit anything under `specs/`, and never run `git` for the spec: the
  workflow does all of it, so every change is reproducible from its run in `.specify/workflows/runs/<run>/`.
- Never answer a gate yourself in interactive mode. Ask the person, with the gate's first option as the recommended
  one.
- Never change the workflow or the helper to get past a gate or a failure; report it.

## Modes

- **Interactive** (default): every gate is asked in Chat with the question tool; committing and pushing are the
  person's choice at the publish gate.
- **Autopilot** (the person set the Copilot session to autopilot; not part of the input): start the run with
  `-i mode=unattended -i publish=push` (with `--repeat`, see Repeat). It takes every recommended choice, does not
  pause, and commits and pushes the spec at the end; go straight to the Report.

The workflow only stops where a person improves the spec or decides what is authored or published: which spec to author
(an unpublished active spec, the proposed candidates), the clarification questions, missing tooling, the fixes of the
analysis, and the publish gate. It moves from spec to plan to tasks to analysis without asking; the publish gate is
where the person reviews the result before anything is committed.

## Running the workflow

Run every command from the repository root as one terminal command, wait for it to finish (agent steps take
minutes; use a long timeout), and never run two at once. Every command needs `TZ=UTC` (AGENTS.md) and an empty
standard input, so that a gate pauses the run instead of waiting for keyboard input in the terminal:

```powershell
$env:TZ = 'UTC'; '' | specify workflow run author --json -i feature="<input>"
$env:TZ = 'UTC'; '' | specify workflow resume <run id> --json -i <input>=<choice> [-i <input>=<choice> ...]
node .github/scripts/speckit-author-flow.mjs pending --run <run id>
```

In a POSIX shell use `TZ=UTC specify workflow run author --json -i feature='<input>' < /dev/null`. Pass the person's
text as the `feature` input unchanged, quoted for the shell (in PowerShell, double every `"` inside double quotes);
leave the input out when it is empty. `run` prints JSON with the `run_id`; remember it.

## Outline

1. **Start.** Say in one sentence what the run will do, then start it.
2. **While the run is paused**, run `pending` and handle what it reports:
   - `kind: "questions"` (a clarification round): ask every question of `questions` with the question tool, in one
     form with one field per question, in order. Use the question as the title, offer each option as
     `<key>) <text>` with the `recommended` key first and marked as recommended, and put `reason` in the
     description. Then resume with one `-i <input>=<key>` per question (`recommended` is accepted as well).
   - `kind: "findings"` (a remediation round of the analysis): ask about every finding of `findings` in one form,
     one field per finding, titled with its severity, id, and summary, with its `fix` in the description and the
     `options` (`apply` first, as the recommended one). Then resume with one `-i <input>=<choice>` per finding.
   - `kind: "gate"` with `input: "publish_verdict"` (the publish gate): this is the review point. Show the person the
     essentials of the spec folder in a few bullets per artifact: the user stories and clarifications of `spec.md`;
     the key decisions and the Architecture Updates of `plan.md`; the phases and `[P]` groups of `tasks.md` (print
     them with `node .github/scripts/speckit-tasks-groups.mjs <folder>`); and the gate's `message` (what is published
     with it, and a warning when the checkout is not on the default branch). Then ask with the gate's options plus
     `revise`: `push` (recommended), `commit`, `none`, `revise`. For `push`, `commit`, or `none` resume with
     `-i publish_verdict=<choice>`. For `revise`, resume with `-i publish_verdict=none` (nothing is committed), ask
     in one form what should change (free text) and in which artifact (`spec`, `plan`, or `tasks`), then start a new
     run with `-i feature=<folder> -i stage=<clarify for spec | plan | tasks> -i feedback="<their answer>"` (it reworks
     that stage, keeps everything before it, and runs everything after it again) and continue at step 2.
   - any other `kind: "gate"`: read the file it names, if any, and show the person its essentials in a few bullets
     (each candidate and why for the proposed specs; the spec folder for an unpublished active spec; the missing
     tooling for the environment gate). Then ask the gate's `message` with its `options`, the first as the
     recommended one, and resume with `-i <input>=<choice>`.
3. **When `pending` reports `completed`:** go to the Report.
4. **When it reports `failed` because the folder's next step does not allow a stage:** show the error and start the
   run again without `stage`, with the feedback only (it reworks the stage before the folder's next step).
5. **When it reports `failed` because clarification questions remain after three rounds:** ask the person the listed
   questions as free text in one form, then start a new run with
   `-i feature=<folder> -i stage=clarify -i feedback="<the questions with their answers>"`, and continue at step 2.
6. **When it reports `failed` because a folder has no `spec.md`:** ask for a feature description and start a new run
   with it (the empty folder stays; tell the person to delete it).
7. **When it reports `failed` otherwise, or `aborted`:** show the `error` as it is, say which stage stopped, and
   recommend the one next step that fits: for missing tooling, authoring the environment spec first (this skill with
   its description); for an agent step that wrote no valid file, running this skill again for the folder (the next
   run starts at the stage that stopped); for a git failure, fixing the checkout (for example checking out a branch)
   and running this skill again for the folder. Do not retry on your own.

## Repeat

With `--repeat <n>` and `n` above 1, run the Outline `n` times in a row, one run at a time and each without a
`feature` input, so every run proposes the next logical spec from the specs that exist by then (a committed spec only
waiting for its push is not continued, and the proposal sees it):

- In autopilot, every run commits its spec and the last one pushes all of them: start runs 1 to `n - 1` with
  `-i mode=unattended -i publish=commit` and run `n` with `-i mode=unattended -i publish=push`.
- Interactive runs ask the publish gate every time, so the person commits or pushes each spec; when a spec is left
  uncommitted, the next run asks whether to continue it or propose a new one.
- Say before each run which of the `n` it is (for example "Authoring spec 2 of 3").
- Stop at the first run that fails, is aborted, or stops for missing tooling (Outline steps 4 to 7 apply to it): do
  not start the remaining runs, and report the authored specs, the stopped run, and its error. In autopilot the specs
  committed so far are not pushed yet: say so, and recommend pushing them (`git push`) or running this skill again.

## Report

End with one short report: the spec folder (as a link), what the run published (`publish` from `pending`: pushed,
committed, or left uncommitted, and the `docs/architecture/` files published with it), the essentials of the spec,
plan, and tasks (the user stories, the key decisions and Architecture Updates, and the phases and `[P]` groups, in a
few bullets), the decisions taken (the gates and the answers; in autopilot, that every decision took the recommended
choice), and exactly one recommended next step: after a push, checking the spec twin that the `Spec Kit prepare`
workflow creates and commenting `/speckit implement` on it; after a commit only, pushing it (`git push`); when left
uncommitted, running this skill again for the folder, which asks the publish gate again. With `--repeat`, report every spec
this way in one report, in the order they were authored (and the stopped run, if any), and end with one recommended
next step for all of them.

---
description: Author one spec until it is ready to implement by running the deterministic Spec Kit workflow `author` and answering its gates in Chat; without input the workflow proposes the next logical spec from the architecture, and at the end it pushes the spec folder so the GitHub automation takes over.
---

## User Input

```text
$ARGUMENTS
```

The input is empty (the workflow offers to continue the active spec, or proposes the next logical spec from
`docs/architecture/`, the existing specs, the spec twin issues, and the code), a feature idea in free text, a spec
folder to continue at its next step, or a spec folder followed by a note: then pass the folder as `feature` and the note
as `feedback` (the run reworks the stage before the folder's next step). A trailing `and push`, `and commit`, or
`no commit` only matters in autopilot (see Modes).

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

- **Interactive** (default): every gate is asked in Chat with the question tool.
- **Autopilot** (the person set the Copilot session to autopilot; not part of the input): start the run with
  `-i mode=unattended` and `-i publish=<push | commit | none>` (`push` for `and push`, `commit` for `and commit`,
  otherwise `none`). It takes every recommended choice and does not pause; go straight to the Report.

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
   - `kind: "gate"`: read the file it names, if any, and show the person its essentials in a few bullets: user
     stories and clarifications for `spec.md`; decisions, structure, and Architecture Updates for `plan.md`; phases
     and `[P]` groups for the task order; each candidate and why for the proposed specs; the spec folder and what is
     published with it for the publish gate. Then ask the gate's `message` with its `options`, the first as the
     recommended one, and resume with `-i <input>=<choice>`.
3. **When `pending` reports `completed`:** go to the Report.
4. **When it reports `failed` with a `revise` stage:** the person chose `revise`. Ask what should change (free text),
   then start a new run with `-i feature=<folder> -i stage=<revise> -i feedback="<their answer>"` (it starts again at
   that stage and keeps everything before it) and continue at step 2.
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

## Report

End with one short report: the spec folder (as a link), what the run published (`publish` from `pending`: pushed,
committed, or left uncommitted, and the `docs/architecture/` files published with it), the decisions taken (the gates
and the answers; in autopilot, that every decision took the recommended choice), and exactly one recommended next step: after a push, checking the spec twin that the
`Spec Kit prepare` workflow creates and commenting `/speckit implement` on it; after a commit only, pushing it; when
left uncommitted, running this skill again for the folder with `and push`, or pushing it by hand.

# Feature Specifications

Each folder is one [Spec Kit](https://github.com/github/spec-kit)
feature created by `/speckit-specify`. A feature folder holds `spec.md` (what
and why), and later `plan.md`, design artifacts, and `tasks.md` from
`/speckit-plan` and `/speckit-tasks`. The governing design stays in
[`docs/architecture/`](../docs/architecture/README.md); specs cite it instead of
restating it, as required by the
[constitution](../.specify/memory/constitution.md).

New specs are authored with `/speckit-gha-author` in Copilot Chat, which runs the
Spec Kit workflow `author`; see [Workflow Per Feature](#workflow-per-feature).

## Index

This file lists no specs on purpose. The spec twin issues are the always-current
index: one issue per feature folder, labelled
[`speckit:spec`](https://github.com/markusheiliger/socalytics/issues?q=is%3Aissue%20label%3Aspeckit%3Aspec).
Each twin links to its spec (and to the archive once it is implemented), carries
a `speckit:stage:*` label for how far the feature has progressed on the default
branch, and holds the feature's native "blocked by" dependencies, which decide
the order in which GitHub automation implements features; see the repository
[README](../README.md#spec-twins). Open twins are features still to be
implemented, closed twins labelled `speckit:stage:implemented` are archived.
Interfaces that later features consume are named in each plan and in
[`docs/architecture/`](../docs/architecture/README.md).

## Archive

Implemented features move to [`archive/`](archive/) under the same folder
name, in the commit that merges their implementation: the implementation
automation does it in its merge, and a local or manual merge runs
`/speckit-gha-archive` first. The move rewrites every reference to the folder
(Markdown links and plain-text paths such as code comments), and the spec twin
follows to the archive after the merge.

## Folder Naming

Feature folders are named `<YYYYMMDD-HHMMSS>-<short-name>`, where the prefix
is the creation time in **UTC**. Timestamps instead of sequential numbers keep
folder names unique when several developers create features on separate
branches, and the prefix also sorts features by creation time. The prefix
carries no dependency meaning; the twin issues record dependency order.

`/speckit-specify` must use the current UTC time for the prefix. Spec Kit's
helper scripts use the machine's local time, so set the environment variable
`TZ=UTC` in the shell when running them directly.

## Workflow Per Feature

### Author

Run `/speckit-gha-author` in GitHub Copilot Chat, with a feature idea, a spec
folder to continue at its next step (optionally followed by a note on what
should change), or nothing: then the run offers to continue the spec you are
working on, or an agent proposes up to three next logical specs from
`docs/architecture/`, the existing specs, the spec twin issues, and the code,
and you pick one. The skill runs the Spec Kit workflow
[`author`](../.specify/extension-src/gha/workflows/author/workflow.yml), which
the `gha` extension ships, and asks you its gates in
Chat; it changes nothing itself. In autopilot the run takes every recommended
choice (`/speckit-gha-author <idea> and push` pushes at the end).
`/speckit-gha-author --repeat 3 and push` authors three proposed specs one after
another, pushing each before the next run proposes the following one; it stops
at the first run that fails.

The same workflow runs in a terminal, where its gates ask directly:

```powershell
$env:TZ = 'UTC'
specify workflow run author -i feature="let club admins see which Analyst Managers are offline"
```

The workflow runs the Spec Kit commands in their order; every decision between
them comes from `node .github/scripts/speckit-author-flow.mjs`, and gates ask
you where a person decides:

1. `/speckit-specify` creates the folder and `spec.md`.
2. `/speckit-clarify`: an agent step writes the questions the skill would ask,
   a gate asks you each one (its recommended option is marked), and the skill
   records your answers; at most three rounds, until no question is left. When
   a fourth scan still finds questions, the run stops and lists them, and you
   answer them in your own words in the next run.
3. `/speckit-plan` selects the technical approach from the architecture and
   checks it against the constitution; a plan without `## Architecture Updates`
   is repaired with `/speckit-gha-architecture`. When the plan needs new
   frameworks, SDKs, or tools, an environment spec must be authored and merged
   first.
4. `/speckit-tasks` writes `tasks.md`; a missing documentation task for the
   Architecture Updates is added by a second pass, and `[P]` group warnings are
   repaired with `/speckit-gha-refine`.
5. `/speckit-analyze` checks the artifacts against each other; a gate asks you
   about each finding with a fix (up to five per round, the most severe first)
   before the approved ones are remediated, at most three analyses.
6. Markdown findings in the spec are fixed by a formatting-only step. The
   publish checks require stage `tasked`, no open clarification markers,
   checklist items, or CRITICAL and HIGH findings, and a clean Markdown check.
7. Your review and last decision: the skill shows the spec, the plan with its
   Architecture Updates, and the phases and `[P]` groups, and you push the spec
   folder and the `docs/architecture/` changes of its plan (committed together
   as `Spec: <title>`), commit them only, leave them uncommitted, or revise
   them. The gate warns when you are not on the default branch.

The run moves from spec to plan to tasks to analysis without asking. Choosing
`revise` at the publish gate commits nothing; `/speckit-gha-author` asks what
should change in the spec, plan, or tasks and starts a new run for that stage.
In a terminal, choose `none` and run it again, for example
`specify workflow run author -i feature=<folder> -i stage=plan -i feedback="split the read model"`:
the run starts again at that stage and keeps everything before it. With
`-i mode=unattended` every decision takes the recommended choice and
`-i publish=push` pushes at the end. Agent steps run the Copilot CLI non-interactively
(`copilot -p … --yolo`); on Windows use the standalone `copilot.exe`.

`node .github/scripts/speckit-author.mjs status [folder]` shows where a spec
stands and what still blocks publishing it.

### Publish And Implement

Once the spec is on the default branch, the `Spec Kit prepare` workflow
creates its twin issue and infers its dependencies on other twins; check them
on GitHub. Then either request implementation on GitHub or implement locally.
On GitHub, comment `/speckit implement` on the twin (its description ends with
the command, ready to copy); the comment records who requested it.
`/speckit-implement` asks which one you want; `/speckit-gha-request`
requests it directly (see the repository
[README](../README.md#requesting-implementation-on-github)).

- **On GitHub:** the request gets a `speckit/<folder>` branch and a draft pull
  request that closes the twin when merged. The workflows implement the tasks
  phase by phase, converge, archive the spec, and merge it, or ask for your
  review when automation cannot continue.
- **Locally:** run `/speckit-implement` and `/speckit-converge` until
  convergence reports completion, then `/speckit-gha-archive` before the
  merge.

The skills locate the active feature through the machine-local
`.specify/feature.json`, which `/speckit-specify` writes. To work on another
folder, point it there, for example
`{ "feature_directory": "specs/<folder>" }`, set the
`SPECIFY_FEATURE_DIRECTORY` environment variable, or pass the folder to
`/speckit-gha-author` (the `feature` input of the `author` workflow).

# Feature Specifications

Each folder is one [Spec Kit](https://github.com/github/spec-kit)
feature created by `/speckit-specify`. A feature folder holds `spec.md` (what
and why), and later `plan.md`, design artifacts, and `tasks.md` from
`/speckit-plan` and `/speckit-tasks`. The governing design stays in
[`docs/architecture/`](../docs/architecture/README.md); specs cite it instead of
restating it, as required by the
[constitution](../.specify/memory/constitution.md).

New specs are authored with `/speckit-gha-author` in Copilot Chat; see
[Workflow Per Feature](#workflow-per-feature).

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

Run `/speckit-gha-author` in GitHub Copilot Chat from the repository root.
Without input it proposes the next logical spec from the architecture, the
existing specs, and the code; with an architecture section (for example
`docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui`) it
authors a spec for that part; with a feature idea in free text it first maps the
idea to the architecture and says when the architecture must change with it;
with a spec folder it resumes where the spec stopped. It runs the Spec Kit skills in their order and pauses for you after
each stage:

1. `/speckit-specify` creates the folder and `spec.md`, and
   `/speckit-clarify` resolves the open questions with you.
2. `/speckit-plan` selects the technical approach from the architecture and
   checks it against the constitution. When the plan needs new frameworks,
   SDKs, or tools, an environment spec is authored and merged first.
3. Optionally `/speckit-checklist` adds a domain checklist.
4. `/speckit-tasks` writes `tasks.md`; its `/speckit-gha-refine` hook
   makes the parallel task groups safe.
5. `/speckit-analyze` checks the artifacts against each other; findings are
   fixed through the Spec Kit skills.
6. The publish checks require stage `tasked`, no open clarification markers or
   checklist items, and a clean Markdown check.
7. Your last decision: commit and push the spec folder.

`node .github/scripts/speckit-author.mjs status [folder]` shows where a spec
stands and what still blocks publishing it.

### Publish And Implement

Once the spec is on the default branch, the `Spec Kit prepare` workflow
creates its twin issue and infers its dependencies on other twins; check them
on GitHub. Then either request implementation on GitHub or implement locally.
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
`/speckit-gha-author`.

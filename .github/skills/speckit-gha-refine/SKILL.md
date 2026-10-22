---
name: speckit-gha-refine
description: Refine the parallel task groups of the active spec's tasks.md so `[P]`
  tasks of one group never change the same files.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Refine Skill

## User Input

```text
$ARGUMENTS
```

This command runs as a mandatory `after_tasks` hook of `/speckit-tasks`, right after `tasks.md` was generated. It can
also be run on its own; the input may then name the spec folder.

The implementation workflow (Spec Kit implement) runs the tasks of each phase one after another in task order,
except that consecutive unchecked `[P]` tasks under one heading run in parallel (at most 5 at once), each from the same commit. Parallel
tasks that change the same file are merged afterwards and verified
together, and redone when their edits conflict, which costs whole runs. This command makes the groups safe and, for a
fresh `tasks.md`, uses the parallelism the plan allows.

## Principles

- Change only `specs/<folder>/tasks.md`. Never change scope, requirements, acceptance criteria, or what a task does;
  only how tasks are grouped, ordered, and how precisely they name their files.
- Never check a task, and never remove or reword a checked task.
- `##` headings are phases: never add, remove, or rename one. Use `###` or `####` headings to separate groups; never
  deeper (they are not headings to the workflow), and never repeat a heading's text within a phase (groups under
  headings with the same text merge).
- Judge by evidence: the task text, the design artifacts, and the code that exists. When the footprint of a task is
  unclear, treat it as overlapping.

## Outline

1. **Locate the spec.** Use the folder from the input, or run
   `python .specify/scripts/python/check_prerequisites.py --json --require-tasks --include-tasks` from the repository
   root and use `FEATURE_DIR`. Read `tasks.md`, `plan.md`, and, when present, `data-model.md`, `contracts/`,
   `research.md`, `quickstart.md`, and `.specify/memory/constitution.md`.

2. **Choose the mode.**
   - **Fresh** (no task is checked and there is no implementation branch yet, that is
     `git ls-remote --heads origin speckit/<folder>` prints nothing; always the case as a hook): full refinement
     (steps 3–8).
   - **In progress** (otherwise): work on the `tasks.md` of the implementation branch `speckit/<folder>`, which is the
     one being implemented. Safety only: fix conflicting groups of unchecked tasks (steps 3–5, 7, 8), but do not form
     new groups, do not move tasks, and do not renumber; a task you split off gets the next free ID (highest + 1) and
     goes directly after its group.

3. **List the groups.** Run `node .github/scripts/speckit-tasks-groups.mjs <path to tasks.md>` from the repository
   root. It prints
   every phase with the order its unchecked tasks will run in, exactly as the workflow plans it, and warnings for
   headings that do not separate groups. Treat its output as the truth about grouping; fix every warning.

4. **Predict each task's footprint**: the files it creates or changes and the symbols it introduces or uses. Start
   from the paths the task names, then add the edits the codebase forces on it, which task texts often leave out:
   - registration and composition: dependency-injection registrations, endpoint or route mapping, middleware,
     startup and host setup, options binding;
   - project and build files: `*.csproj`, `Directory.Packages.props`, `Directory.Build.*`, `*.slnx`, `package.json`;
   - shared configuration and contracts: `appsettings*.json`, OpenAPI or schema documents, error and problem-type
     catalogs, shared constants and enums;
   - persistence: migrations, their numbering, and shared database fixtures;
   - shared tests and docs: test hosts and fixtures, documentation indexes and READMEs.
   Find these in `plan.md` (project structure), the artifacts, and the existing code (search for the registration
   methods, project files, and fixtures the task's area uses).

5. **Make every group safe.** A group is safe when its tasks' footprints are disjoint and no task uses something a
   sibling creates. For each unsafe group, apply the least invasive fix that removes the overlap:
   1. The overlap is only unnamed: name the shared file in the tasks, and still treat it as an overlap.
   2. The overlap is a small shared edit (for example registering several components): remove that edit from the
      grouped tasks and add one task without `[P]` after the group that does it for all of them, citing the same
      requirements. The heavy work stays parallel.
   3. The tasks depend on each other or overlap broadly: remove `[P]` from the tasks that must run alone, in an
      order that respects the dependencies.
   A group whose tasks would only overlap in `tasks.md` itself is safe.

6. **Use the parallelism (fresh mode only).**
   - Mark `[P]` on further tasks whose footprints are disjoint from their neighbours' and that depend on no
     unfinished task, so they join or form a group.
   - Within a phase, move tasks only where every dependency still runs earlier, to bring independent tasks together
     or to put a shared follow-up task after its group.
   - Separate groups that follow each other with a `###` heading each, named after what the group does (for example
     `### Device-key providers` and `### Provider registration`).
   - Renumber the task IDs to run in file order (T001, T002, …) and update every reference to a renumbered ID in
     `tasks.md` (dependency notes, parallel examples, strategy).

7. **Keep the document consistent.** The `[P]` legend must say that consecutive `[P]` tasks under one heading run in
   parallel, each from the same commit (replace wording such as "informational only"). Update the dependency notes and
   the parallel examples to match the new groups.

8. **Check the result.** Run the helper again: no warnings, and every set of `[P]` tasks that run in parallel is one you judged safe. Re-read
   each changed task against its requirements.

9. **Report.** List each group of the final `tasks.md` per phase as `kept`, `new`, `split` (with the follow-up task), or
   `ended`, with the reason and the files that overlapped, followed by any renumbering. When nothing needed a change,
   say so and leave `tasks.md` as it is.
   - As a hook: report and return, so `/speckit-tasks` completes its report.
   - On its own, in progress mode: show the diff and ask before saving. Do not commit or push; the person brings the
     change to the implementation branch, for example through an amendment pull request.

## Done When

- [ ] The helper reports no warnings for `tasks.md`.
- [ ] Consecutive `[P]` tasks that run in parallel have disjoint footprints and no dependencies on each other.
- [ ] No requirement, scope, or checked task changed; only `tasks.md` changed.
- [ ] The legend, dependency notes, and parallel examples match the groups.

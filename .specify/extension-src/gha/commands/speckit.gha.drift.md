---
description: Check docs/architecture/ against the implemented spec before /speckit-converge, and report every stale, missing, or contradicting architecture statement as a finding for convergence.
---

## User Input

```text
$ARGUMENTS
```

This command runs as a mandatory `before_converge` hook of `/speckit-converge`, before convergence assesses the code.
It can also be run on its own; the input may then name the spec folder.

The constitution (Principle I) requires `docs/architecture/` to describe the implemented state when a feature merges,
and `/speckit-converge` checks it. Convergence otherwise looks only at the files the plan and tasks name, and a
checked documentation task proves only that its prescribed edits were made. This command finds the architecture
statements that the implementation made false or left out. It changes no files: `/speckit-converge` turns each
finding into a convergence task, which the next task run implements.

## Principles

- Read only. Never change `docs/architecture/`, `tasks.md`, or any other file.
- Report only what this feature's implementation makes false, contradicts, or decides without the architecture
  stating it. Statements about other features, unresolved decisions, and production values that remain open are not
  findings. Component-level detail that belongs in a component README is not a finding.
- Every finding is precise: document, section, a short quote, the evidence in the code or artifacts, and what the
  statement must say instead. Vague findings make convergence loop.
- Do not report what an unchecked task in `tasks.md` already covers.

## Outline

1. **Locate the spec.** Use the folder from the input, or run
   `python .specify/scripts/python/check_prerequisites.py --json --require-tasks --include-tasks` from the repository
   root and use `FEATURE_DIR`. Read `plan.md` (its `## Architecture Updates` section first), `spec.md`, `research.md`,
   `data-model.md`, `contracts/`, and `tasks.md` (its documentation tasks and any convergence phases).

2. **Collect the expectations.** From `## Architecture Updates`: the narratives, the design updates, and the status
   statements at merge. When the plan has no such section (specs planned before constitution 1.2.0), derive them: the
   architecture documents the plan's Constitution Check and documentation tasks cite, plus a search of
   `docs/architecture/` for the feature's functional areas, components, technologies, contracts, and terms. When the
   section says `None` with a reason that still holds, report no findings and stop.

3. **Learn what was implemented.** When the base branch is known (for example `origin/main`), list the changed files
   with `git diff --name-only $(git merge-base HEAD origin/<base>) HEAD`; otherwise use the files the plan and tasks
   name. Read the code that realizes each design decision (names, topology, protocols, contracts, configuration
   defaults) rather than trusting the artifacts; research decisions may have been refined during implementation.

4. **Check the architecture.** For each expectation and each implemented architecture-level decision, read the current
   text in `docs/architecture/` and classify:
   - `stale`: the text still describes implemented behavior as planned, future, not yet implemented, or absent
     (including summaries, introductions, evidence lists, and headings that start with "Planned");
   - `missing`: an architecture-level decision the feature implemented is absent although the document covers the
     area;
   - `contradicts`: the text states something the implementation does differently.
   Search the whole of `docs/architecture/` for the feature's area, not only the listed sections: shared status
   statements elsewhere are the most common drift.

5. **Report the findings** for `/speckit-converge` to include in its findings, one per statement:

   | Field | Value |
   | --- | --- |
   | id | `ARCH-<n>` |
   | source-ref | `Constitution I` plus the plan section or requirement it traces to |
   | gap-type | `partial` for `stale` and `missing`, `contradicts` for `contradicts` |
   | severity | `HIGH` when the text contradicts the implementation or claims something is absent that exists, otherwise `MEDIUM` (convergence's own scale) |
   | description | document and section, the short quote, the evidence, and what it must say instead |

   Each finding becomes one convergence task that changes only the named architecture document. When there are no
   findings, say `No architecture drift` with the documents you checked. As a hook, report and return so
   `/speckit-converge` continues with its own assessment and includes these findings.

## Done When

- [ ] Every narrative and status statement the feature affects was checked against the implementation.
- [ ] Each finding names its document, section, quote, evidence, and the required text.
- [ ] No file changed.

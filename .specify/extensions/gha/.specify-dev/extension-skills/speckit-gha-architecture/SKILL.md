---
name: speckit-gha-architecture
description: Make the active spec's plan.md name its effect on docs/architecture/
  in an Architecture Updates section, so the implementation keeps the architecture
  true for the merged state.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Architecture Skill

## User Input

```text
$ARGUMENTS
```

This command runs as a mandatory `after_plan` hook of `/speckit-plan`, right after `plan.md` and its design artifacts
were written. It can also be run on its own; the input may then name the spec folder.

The constitution (Principle I) makes `docs/architecture/` the authority for the current system design and requires
every plan to state its effect on it in an `## Architecture Updates` section. At merge, the architecture must describe
the implemented state. Documentation tasks that only substitute prescribed sentences left planned and unimplemented
statements behind, so the section also lists the status statements that the feature makes true or false anywhere in
`docs/architecture/`. `node .github/scripts/speckit-author.mjs status` refuses to publish a spec without the section.

## Principles

- Change only `specs/<folder>/plan.md`, and, for design-level updates the plan decides now, the owning narratives in
  `docs/architecture/` (constitution Principle I: in the same change). Never change requirements, scope, or other
  artifacts.
- Evidence first: name the exact documents, sections, and sentences. Search, do not guess.
- Keep unresolved decisions and production values explicit; never present a target state as implemented.

## Outline

1. **Locate the spec.** Use the folder from the input, or run
   `python .specify/scripts/python/check_prerequisites.py --json` from the repository root and use `FEATURE_DIR`. Read
   `spec.md`, `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md` (when present), and
   `.specify/memory/constitution.md`.

2. **Find the affected narratives.** Read `docs/architecture/README.md` and the documents the plan's Constitution
   Check and Technical Context cite. For each functional area, component, technology, contract, and term the feature
   touches, search `docs/architecture/` (for example `grep -rn` for its names and identifiers) and note every section
   that describes it.

3. **Find the status statements.** In those documents, and in every other document that mentions the feature's area,
   search for statements whose truth the merge changes: wording such as "planned", "future", "will", "not yet",
   "unimplemented", "remain", "scaffold", "only … is executable", headings that start with "Planned", and lists of
   evidence or of what does not exist. Include statements in summaries and introductions, not only in the area's own
   section.

4. **Write `## Architecture Updates`** in `plan.md`, after the Constitution Check, with three parts:
   - **Narratives**: each document and section the feature implements or changes, with a link.
   - **Design updates**: decisions of this plan that the architecture does not state yet or states differently (a new
     component, technology, topology, protocol, contract, or term). Apply design-level updates to the owning narrative
     now, worded as decided but not implemented, and list each; say which ones the implementation must turn into
     present tense.
   - **Status statements at merge**: each statement from step 3 that the merged feature makes false, with its
     document, section, and a short quote, and what it must say instead.

   For a feature without architectural effect (for example an environment spec that only changes the environment
   actions), write `None` followed by the reason, and skip step 5.

5. **Require an outcome-based documentation task.** Add to the section a sentence for `/speckit-tasks` that the
   documentation task must make the listed narratives and every status statement the feature affects true for the
   merged state, re-reading the documents at implementation time, with the listed rewrites as examples rather than the
   whole job. When `tasks.md` already exists, check that one unchecked task references `docs/architecture/` and says
   this; if not, report it so the person reruns `/speckit-tasks` or amends the task (this command never changes
   `tasks.md`).

6. **Check the result.** Every narrative from step 2 and every statement from step 3 appears in the section, or the
   section says `None` with a reason. Run `node .github/scripts/check-markdown.mjs`.

7. **Report** the narratives, design updates, and status statements you listed, and the architecture files you
   changed. As a hook, report and return so `/speckit-plan` completes its report.

## Done When

- [ ] `plan.md` has `## Architecture Updates` with narratives, design updates, and status statements, or `None` with a
  reason.
- [ ] Design-level updates the plan decides are applied to their owning narratives.
- [ ] The section requires an outcome-based documentation task.
- [ ] No requirement or scope changed.

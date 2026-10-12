---
description: Guide the user in Copilot Chat from a feature idea, an architecture section, or an existing spec folder through the Spec Kit authoring flow (specify, clarify, plan, tasks, analyze) to a pushed spec that the GitHub workflows can implement.
---

## User Input

```text
$ARGUMENTS
```

The input is one of:

- **empty**: propose the next logical spec from the architecture, the existing specs, and the implementation;
- **an architecture section**, for example `docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui`
  (a path into `docs/architecture/`, optionally with a heading anchor): author a spec for that part of the
  architecture;
- **a spec folder**, for example `20261012-134500-fleet-status` or `specs/20261012-134500-fleet-status`: continue
  that spec at its next Spec Kit step;
- **a feature idea** in free text, for example `let club admins see which Analyst Managers are offline`: author a spec
  for it.

Recognize the kind by its form: a path into `docs/architecture/` is a section; a name that exists as a folder under
`specs/` or `specs/archive/` is a spec folder; anything else is a feature idea. For a section or folder, text after the
first token is a note from the user; take it into account.

## Rules

- **Follow the Spec Kit flow in its order:** `/speckit-specify` → `/speckit-clarify` → `/speckit-plan` → optional
  `/speckit-checklist` → `/speckit-tasks` → `/speckit-analyze`. Stop before `/speckit-implement`: implementation runs
  on GitHub after the push.
- **Run every step through the repository's Spec Kit skill.** For step `<name>`, load
  `.github/skills/speckit-<name>/SKILL.md` and follow it exactly, with the arguments named below as its user input.
  Do not paraphrase, shorten, or replace a Spec Kit step with your own logic. Hooks (`before_*`, `after_*`), including
  the mandatory `after_tasks` hook `/speckit-gha-refine`, run from those skills as they prescribe; never run them
  yourself in addition.
- **Change spec artifacts only through Spec Kit.** Do not edit `spec.md`, `plan.md`, `tasks.md`, design artifacts, or
  checklists yourself. Changes come from the Spec Kit skills, including the remediation edits that `/speckit-analyze`
  proposes and the user approves. Formatting-only fixes for the Markdown check are the only exception.
- **Never tick a checklist item yourself.** A checked item records a person's verification; tick one only when the
  user confirms that item.
- **Repository rules:** run the `/speckit-specify` script with UTC time in the same command (`$env:TZ='UTC'; python …`
  in PowerShell, `TZ=UTC python …` in a POSIX shell), as `AGENTS.md` requires. Never edit files under `.specify/` or
  `.github/`. Never run `/speckit-implement`, `/speckit-taskstoissues`, or `/speckit-gha-request` from this command.
- **Ask with the question tool** when the chat offers one, one question at a time, with the recommended choice first.
- **Pauses:** after specify and clarify, after plan, after tasks, and after analyze, summarize the result and ask
  whether to continue, revise, or stop. Stopping is always safe: `/speckit-gha-author <folder>` resumes later.

## Outline

### 1. Resolve the input

**Spec folder.** Run `node .github/scripts/speckit-author.mjs status <folder>` from the repository root and continue
at the reported step:

| Next step | Continue with |
| --- | --- |
| `specify` | the folder has no `spec.md`: ask for the feature description, then step 2 |
| `clarify` | step 2b |
| `plan` | step 3 |
| `tasks` | step 4 |
| `analyze` | step 5 (analyze leaves no trace, so a tasked, unpublished spec is always analyzed again) |
| `published` | report that the spec is pushed and the GitHub workflows own it; stop |
| `done` | report that the spec is implemented or archived; stop |

**Architecture section.** Read the section, the sections it links to, and the documents that govern it. Check `specs/`, `specs/archive/`, and the twin issues for specs that already cover it:

```powershell
gh issue list --label speckit:spec --state all --json number,title,state,labels --limit 200
```

If one does, say so and offer to continue that spec or to author a follow-up slice. Otherwise draft the feature
description (step 1a).

**Feature idea.** Find where the idea belongs in the architecture: the documents and sections that describe it or the
behavior it changes, and the specs, twins, and code that already cover parts of it. Then:

- **Covered by the architecture:** continue as for an architecture section with those sections.
- **Covered by an existing spec:** say so and offer to continue that spec or to author a follow-up slice.
- **Not covered, or in conflict:** say which architecture narratives the idea extends or contradicts. The constitution
  makes `docs/architecture/` the design authority, so the spec must name those narratives and its plan must update
  them in the same change. Ask whether to continue on that basis, to change the architecture first in a separate
  change, or to stop.

Then draft the feature description (step 1a), naming the architecture sections it implements or must update.

**Empty input.** If `.specify/feature.json` points to a spec that `speckit-author.mjs status` does not report as
`published` or `done`, ask first whether to continue it. Otherwise find the next logical spec:

1. Read `AGENTS.md` ("Current State"), `docs/architecture/README.md` and the architecture documents, `specs/README.md`,
   every `spec.md` under `specs/` and `specs/archive/`, and the twin issues.
2. Check the implementation where a claim matters, for example whether an endpoint, table, or project exists under
   `src/`.
3. List the gaps: behavior the architecture describes that no spec covers and the code does not implement. Skip
   anything the architecture marks as unresolved, deferred, or awaiting evidence unless a spec can resolve it.
4. Rank the gaps: dependencies already merged or implemented first, then value for the first release, then the
   smallest independently testable slice (as the constitution requires), then risk.
5. Present the top three, each with: a title; the architecture sections (as links); what already exists; the specs
   or twins it depends on; why it is next; the rough scope and what stays out; and whether it needs new frameworks,
   SDKs, or tools (an environment spec first). Let the user pick one, adjust one, or describe another.

Create nothing before the user has chosen.

**1a. Feature description.** Write the description `/speckit-specify` receives: the user value, the scope and what
stays out, the dependencies on other specs, and the architecture sections it implements (the spec cites them instead
of restating them). Show it and let the user confirm or adjust it.

### 2. Specify and clarify

a. Run `/speckit-specify` with the confirmed description.

b. Run `/speckit-clarify` for the new spec. Let it ask its questions. When it reports outstanding or deferred items,
   ask the user whether to run it again; repeat until nothing material is open.

**Pause:** summarize the spec (user stories, key requirements, clarifications, out of scope, dependencies) and ask:
continue to plan (recommended), clarify again, or stop.

### 3. Plan

Run `/speckit-plan`.

**Environment check.** Compare the frameworks, SDKs, tools, and services the plan needs to build and test the feature
with what `.github/actions/environment-setup` and `.github/actions/environment-verify` provide and with the commands in
`AGENTS.md`. If something new is needed, stop and explain what. Offer to author the environment spec first with
`/speckit-gha-author` and a description of the tooling change: the environment spec changes only those two action
folders and is merged before this spec. Once it exists, return to this spec and record the dependency through
`/speckit-clarify`, with the environment spec folder as the answer to the dependency question, then rerun
`/speckit-plan` if the plan must change.

**Pause:** summarize the plan (technical decisions, structure, contracts, migrations, dependencies) and ask: continue
to tasks (recommended), add a domain checklist with `/speckit-checklist`, revise the plan, or stop. Each checklist must
be fully checked before the spec is published, so add only checklists the user intends to resolve.

### 4. Tasks

Run `/speckit-tasks`; its `after_tasks` hook refines the `[P]` groups. Then run
`node .github/scripts/speckit-tasks-groups.mjs <folder>` and show the phases and their task order, including warnings.

**Pause:** summarize the phases, task count, and parallel groups, and ask: continue to analyze (recommended), revise
the tasks, or stop.

### 5. Analyze

Run `/speckit-analyze`. Handle its findings as it prescribes: when it offers remediation and the user accepts, apply
exactly the approved edits, or rerun the Spec Kit step it names (`/speckit-specify` with refinement,
`/speckit-clarify`, `/speckit-plan`, or `/speckit-tasks`). Run `/speckit-analyze` again until no CRITICAL or HIGH
finding remains or the user accepts the remaining ones.

**Pause:** summarize the remaining findings and ask: continue to the publish checks (recommended), revise, or stop.

### 6. Publish checks

Run `node .github/scripts/speckit-author.mjs status <folder>` and resolve every publish blocker:

- **Stage is not `tasked`:** continue at the reported Spec Kit step.
- **Open `[NEEDS CLARIFICATION]` markers:** run `/speckit-clarify`.
- **Open checklist items:** go through them with the user. Gaps in the spec go back to the Spec Kit step that owns
  them (`/speckit-clarify` for `requirements.md`); tick an item only when the user confirms it.
- **Missing `specs/README.md` entry:** add a row for the spec to the matching table (feature link, purpose, depends
  on) and show it. `specs/README.md` is a repository overview, not a Spec Kit artifact.

Then run `node .github/scripts/check-markdown.mjs` and fix what it reports (formatting only inside the spec folder).

### 7. Commit and push

This is the last decision of the flow. Show the files that would be committed
(`git status --short -- specs/<folder> specs/README.md`), the current branch, and the default branch. If the current
branch is not the default branch, say that the spec twin is only created once the spec reaches the default branch.

Ask: commit and push (recommended), commit only, or stop.

- Stage only `specs/<folder>/` and `specs/README.md`, commit with the message `Spec: <spec title>` (add the repository's
  co-author trailer when it applies), and push to the branch's upstream (`git push`, or `git push -u origin <branch>`
  when it has none). Never force-push.
- Report the commit and the push result. After the push, the GitHub workflows take over: they create the spec twin,
  infer its dependencies, and track its stage. Do not request implementation from this command.

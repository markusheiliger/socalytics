---
description: Guide the user in Copilot Chat from a feature idea, an architecture section, an existing spec folder, or a request for the next logical specs through the full Spec Kit authoring flow (specify, clarify, plan, tasks, analyze, publish checks) to specs that only need to be committed and pushed, interactively or autonomously in autopilot.
---

## User Input

```text
$ARGUMENTS
```

The input is one of:

- **empty**: propose the next logical spec from the architecture, the existing specs, and the implementation;
- **a batch request**: a count of next logical specs in free text, for example `the 3 logical next specs`,
  `the logical next 5 specs`, `next 2 specs`, or `the five logical next steps`: find that many next logical specs and
  author them one after another (see Batch under step 1);
- **an architecture section**, for example `docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui`
  (a path into `docs/architecture/`, optionally with a heading anchor): author a spec for that part of the
  architecture;
- **one or more spec folders**, for example `20261012-134500-fleet-status` or `specs/20261012-134500-fleet-status`:
  continue each spec at its next Spec Kit step, one after another in the given order (several folders form a batch);
- **a feature idea** in free text, for example `let club admins see which Analyst Managers are offline`: author a spec
  for it.

Recognize the kind by its form: a path into `docs/architecture/` is a section; a name that exists as a folder under
`specs/` or `specs/archive/` is a spec folder (consecutive such names are several folders); text that asks for a number (in digits or words) of next, logical, or
upcoming specs or steps, without describing a feature, is a batch request; anything else is a feature idea. For a
section or folders, the text after them is a note from the user; take it into account. A batch count must be
between 1 and 10; ask for (or, in autopilot, use) 10 when a larger count is given.

## Modes

- **Interactive** (default): ask at every decision with the question tool, one question at a time, with the
  recommended choice first, and pause where the outline says **Pause**.
- **Autopilot**: the user has set the Copilot session to autopilot mode. The mode belongs to the session, not to this
  command's input; the input has no flag for it. Never ask. At every decision, take the choice this command or the
  Spec Kit skill marks as recommended (or suggested) and continue; at every **Pause**, print a short summary of the
  stage and move on. Say once at the start that autopilot answers every decision with its recommended choice. Record
  each answered decision as the skills prescribe (for example clarification answers in the `## Clarifications` section
  of `spec.md`), so a person can review every autonomous choice in the artifacts.

Autopilot carries each spec through every stage to step 6, so that the spec only needs to be committed and pushed.
Committing and pushing remain a person's decision (see step 7), because `AGENTS.md` forbids
committing or pushing without an explicit request.

## Rules

- **Follow the Spec Kit flow in its order:** `/speckit-specify` → `/speckit-clarify` → `/speckit-plan` → optional
  `/speckit-checklist` → `/speckit-tasks` → `/speckit-analyze`. Stop before `/speckit-implement`: implementation runs
  on GitHub after the push.
- **Run every step through the repository's Spec Kit skill.** For step `<name>`, load
  `.github/skills/speckit-<name>/SKILL.md` and follow it exactly, with the arguments named below as its user input.
  Do not paraphrase, shorten, or replace a Spec Kit step with your own logic. Hooks (`before_*`, `after_*`), including
  the mandatory `after_plan` hook `/speckit-gha-architecture` and the mandatory `after_tasks` hook
  `/speckit-gha-refine`, run from those skills as they prescribe; never run them yourself in addition.
- **Create every artifact.** A spec is complete only with `spec.md`, `checklists/requirements.md`, `plan.md` with its
  `## Architecture Updates` section, the design artifacts the plan produces (`research.md`, and `data-model.md`,
  `contracts/`, and `quickstart.md` where the plan calls for them), and `tasks.md`, all from their Spec Kit skills.
- **Change spec artifacts only through Spec Kit.** Do not edit `spec.md`, `plan.md`, `tasks.md`, design artifacts, or
  checklists yourself. Changes come from the Spec Kit skills, including the remediation edits that `/speckit-analyze`
  proposes and the user approves (in autopilot: that it recommends). Formatting-only fixes for the Markdown check are
  the only exception.
- **Never tick a checklist item yourself.** `/speckit-specify` and `/speckit-clarify` maintain
  `checklists/requirements.md` themselves: they mark an item when the spec passes it. Any other checked item records a
  person's verification; tick one only when the user confirms that item. Autopilot therefore adds no domain checklist
  with `/speckit-checklist`.
- **Repository rules:** run the `/speckit-specify` script with UTC time in the same command (`$env:TZ='UTC'; python …`
  in PowerShell, `TZ=UTC python …` in a POSIX shell), as `AGENTS.md` requires. Never edit files under `.specify/` or
  `.github/`. Never run `/speckit-implement`, `/speckit-taskstoissues`, or `/speckit-gha-request` from this command.
- **Ask with the question tool** in interactive mode, one question at a time, with the recommended choice first.
- **Pauses:** after specify and clarify, after plan, after tasks, and after analyze, summarize the result. In
  interactive mode ask whether to continue (recommended), revise, or stop; in autopilot continue. Stopping is always
  safe: `/speckit-gha-author <folder>` resumes later.
- **Bounded loops:** run `/speckit-clarify` at most three times and `/speckit-analyze` at most three times per spec
  in autopilot. When the limit is reached with material items open, stop that spec and report them (see
  step 8) instead of looping.

## Outline

### 1. Resolve the input

**Spec folders.** For each folder in the given order, run `node .github/scripts/speckit-author.mjs status <folder>` from
the repository root and continue at the reported step; several folders are authored as a batch (see Batch below), so
step 7 comes once for all of them:

| Next step | Continue with |
| --- | --- |
| `specify` | the folder has no `spec.md`: ask for the feature description (autopilot: derive it from the folder name and the architecture as in step 1a), then step 2 |
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

If one does, say so and offer to continue that spec (recommended when it is not published or done) or to author a
follow-up slice (recommended otherwise). Otherwise draft the feature description (step 1a).

**Feature idea.** Find where the idea belongs in the architecture: the documents and sections that describe it or the
behavior it changes, and the specs, twins, and code that already cover parts of it. Then:

- **Covered by the architecture:** continue as for an architecture section with those sections.
- **Covered by an existing spec:** say so and offer to continue that spec or to author a follow-up slice, recommended
  as for an architecture section.
- **Not covered, or in conflict:** say which architecture narratives the idea extends or contradicts. The constitution
  makes `docs/architecture/` the design authority, so the spec must name those narratives and its plan must update
  them in the same change. Ask whether to continue on that basis (recommended), to change the architecture first in a
  separate change, or to stop.

Then draft the feature description (step 1a), naming the architecture sections it implements or must update.

**Empty input.** If `.specify/feature.json` points to a spec that `speckit-author.mjs status` does not report as
`published` or `done`, ask first whether to continue it (recommended). Otherwise find the next logical spec with the
Discovery below and present the top three. Let the user pick one (the top-ranked one is recommended), adjust
one, or describe another.

**Batch request.** Find the requested number of next logical specs with the Discovery below, present them,
and author them as described in Batch below.

Create nothing before the user has chosen (in autopilot: before the discovery has ranked the candidates).

#### Discovery

1. Read `AGENTS.md` ("Current State"), `docs/architecture/README.md` and the architecture documents, `specs/README.md`,
   every `spec.md` under `specs/` and `specs/archive/`, and the twin issues. Run `git fetch` first and work on an
   up-to-date default branch, so the discovery sees specs and implementations that merged recently.
2. Check the implementation where a claim matters, for example whether an endpoint, table, or project exists under
   `src/`.
3. List the gaps: behavior the architecture describes that no spec covers and the code does not implement. Skip
   anything the architecture marks as unresolved, deferred, or awaiting evidence unless a spec can resolve it.
4. Rank the gaps: dependencies already merged or implemented first, then value for the first release, then the
   smallest independently testable slice (as the constitution requires), then risk. A gap that needs new frameworks,
   SDKs, or tools ranks its environment spec directly before it.
5. Present the candidates, each with: a title; the architecture sections (as links); what already exists; the specs
   or twins it depends on (including earlier candidates of the same batch); why it is next; the rough scope and what
   stays out; and whether it needs new frameworks, SDKs, or tools (an environment spec first).

**1a. Feature description.** Write the description `/speckit-specify` receives: the user value, the scope and what
stays out, the dependencies on other specs, and the architecture sections it implements (the spec cites them instead
of restating them). Show it and let the user confirm (recommended) or adjust it.

#### Batch

A batch authors the requested number of specs one after another, each through steps 1a to 6, in the ranked order:

1. Present the ranked candidates and their feature descriptions together, and ask whether to author them in this order
   (recommended), to adjust the selection or a description, or to stop. Autopilot takes the ranked set.
2. Author one spec at a time. Finish a spec through step 6 before you run `/speckit-specify` for the next one, because
   `.specify/feature.json` names the one active spec. Interactive mode keeps every pause of every spec.
3. Before each later spec, read the specs authored earlier in the batch as existing specs: they count for coverage and
   dependencies even though they are not committed yet. Reference a batch sibling as a dependency by its folder, and
   never duplicate its scope.
4. When a plan needs an environment spec (step 3), author it next, as an extra spec ahead of the dependent one; it does
   not count toward the requested number.
5. When a spec stops at a bounded loop or a blocker, do not author the specs that depend on it; continue with the
   independent ones and report the rest as not started.

### 2. Specify and clarify

a. Run `/speckit-specify` with the confirmed description.

b. Run `/speckit-clarify` for the new spec to improve it. Let it ask its questions; in autopilot answer each with its
   recommended option (or its suggested short answer). When it reports outstanding or deferred items, or changed the
   spec so that new ambiguities may have appeared, run it again (in interactive mode: ask whether to, recommended);
   repeat until it reports no critical ambiguities and nothing material is outstanding.

**Pause:** summarize the spec (user stories, key requirements, clarifications, out of scope, dependencies) and ask:
continue to plan (recommended), clarify again, or stop.

### 3. Plan

Run `/speckit-plan`; its `after_plan` hook writes the `## Architecture Updates` section of `plan.md` (constitution
Principle I) and applies the design-level updates to `docs/architecture/`.

**Environment check.** Compare the frameworks, SDKs, tools, and services the plan needs to build and test the feature
with what `.github/actions/environment-setup` and `.github/actions/environment-verify` provide and with the commands in
`AGENTS.md`. Count an environment spec that is authored but not yet merged (in this batch or on the default branch) as
providing what it specifies. If something new is needed, stop and explain what. Offer to author the environment spec
first with `/speckit-gha-author` and a description of the tooling change (recommended): the environment spec changes
only those two action folders and is merged before this spec. Once it exists, return to this spec and record the
dependency through `/speckit-clarify`, with the environment spec folder as the answer to the dependency question, then
rerun `/speckit-plan` if the plan must change.

**Pause:** summarize the plan (technical decisions, structure, contracts, migrations, dependencies, and the
architecture narratives, design updates, and status statements of its Architecture Updates section) and ask: continue
to tasks (recommended), add a domain checklist with `/speckit-checklist`, revise the plan, or stop. Each checklist must
be fully checked by a person before the spec is published, so add only checklists the user intends to resolve.

### 4. Tasks

Run `/speckit-tasks`; its `after_tasks` hook refines the `[P]` groups. Then run
`node .github/scripts/speckit-tasks-groups.mjs <folder>` and show the phases and their task order, including warnings.
Warnings that remain go back to `/speckit-tasks`.

**Pause:** summarize the phases, task count, and parallel groups, and ask: continue to analyze (recommended), revise
the tasks, or stop.

### 5. Analyze

Run `/speckit-analyze`. Handle its findings as it prescribes: when it offers remediation, accept it (recommended;
autopilot always accepts) and apply exactly the proposed edits, or rerun the Spec Kit step it names
(`/speckit-specify` with refinement, `/speckit-clarify`, `/speckit-plan`, or `/speckit-tasks`). Remediate findings of
every severity that have a concrete fix. Run `/speckit-analyze` again until no CRITICAL or HIGH finding remains and
every remaining finding is accepted (in autopilot: until a run reports no finding with a concrete fix).

**Pause:** summarize the remaining findings and ask: continue to the publish checks (recommended), revise, or stop.

### 6. Publish checks

Run `node .github/scripts/speckit-author.mjs status <folder>` and resolve every publish blocker:

- **Stage is not `tasked`:** continue at the reported Spec Kit step.
- **Open `[NEEDS CLARIFICATION]` markers:** run `/speckit-clarify`.
- **Open checklist items:** in `checklists/requirements.md`, run `/speckit-clarify` for the gaps the open items name;
  it re-validates the checklist. For any other checklist, go through the items with the user and tick an item only
  when the user confirms it; in autopilot, report the checklist as open (step 8) instead.
- **Missing `specs/README.md` entry:** add a row for the spec to the matching table (feature link, purpose, depends
  on) and show it. `specs/README.md` is a repository overview, not a Spec Kit artifact.
- **Missing `## Architecture Updates` in `plan.md`:** rerun `/speckit-plan` (its `after_plan` hook writes the
  section), or run `/speckit-gha-architecture <folder>` for a plan that is otherwise final.
- **No task references `docs/architecture/`:** rerun `/speckit-tasks` so it generates the outcome-based
  documentation task that the Architecture Updates section requires (not needed when the section says `None`).

Then run `node .github/scripts/check-markdown.mjs` and fix what it reports (formatting only inside the spec folder),
and run `speckit-author.mjs status` again: the spec is ready when it reports no publish blockers.

### 7. Commit and push

This is the last decision of the flow; in a batch it comes once, after every spec of the batch passed step 6. Show the
files that would be committed (`git status --short -- specs/<folder> specs/README.md` for each spec), the current
branch, and the default branch. If the current branch is not the default branch, say that the spec twin is only
created once the spec reaches the default branch.

In interactive mode ask: commit and push (recommended), commit only, or stop. In autopilot, commit and push only when
the input explicitly asked for it (for example `and push`); otherwise stop here with every spec ready to commit, and
report the command that resumes the flow at this decision.

- Before committing, run `git fetch` and, when the branch is behind its upstream, fast-forward it
  (`git pull --ff-only`); never rebase or merge over local spec work without asking.
- Commit each spec separately, in batch order: stage only `specs/<folder>/` and the `specs/README.md` row of that spec,
  and commit with the message `Spec: <spec title>` (add the repository's co-author trailer when it applies). Then push
  to the branch's upstream (`git push`, or `git push -u origin <branch>` when it has none). Never force-push.
- Report the commits and the push result. After the push, the GitHub workflows take over: they create the spec twins,
  infer their dependencies, and track their stage. Do not request implementation from this command.

### 8. Report

End with one report for the whole run:

- each spec with its folder (as a link), title, stage, and `speckit-author.mjs status` result: ready to commit,
  committed and pushed, or stopped (with the reason, the open items, and the step to resume);
- the decisions autopilot answered with the recommended choice, grouped by spec and stage (the clarifications are also
  recorded in each `spec.md`), so a person can review them before publishing;
- batch candidates that were not started, and why;
- exactly one recommended next action: usually committing and pushing the ready specs with
  `/speckit-gha-author <folders in batch order> and push` or reviewing a named open item.

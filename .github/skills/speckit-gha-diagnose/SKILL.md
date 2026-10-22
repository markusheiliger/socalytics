---
name: speckit-gha-diagnose
description: Diagnose why a Spec Kit implementation on GitHub stopped and decide how
  to continue; amend the spec artifacts only when they are the cause.
compatibility: Requires spec-kit project structure with .specify/ directory
metadata:
  author: socalytics
  source: extension:gha
---

# Gha Diagnose Skill

## User Input

```text
$ARGUMENTS
```

The input may name the implementation pull request (`#49` or `49`), the twin issue, or the spec folder, and may add
notes. Without any of them, use the open implementation pull request of the active feature from
`.specify/feature.json`.

**Interaction.** Run **non-interactive** when `.speckit-diagnosis/evidence.md` exists (the `Spec Kit diagnose` agentic
workflow started you): never ask, and turn every open question into a report question. Otherwise run
**interactive**: ask the user (one question at a time, with choices) whenever a decision is theirs.

**Mode** (non-interactive: `mode` in `.speckit-diagnosis/context.json`):

| Mode | Workspace | Task |
| --- | --- | --- |
| `diagnose` | the implementation branch `speckit/<folder>` | diagnose the stop (steps 1–6); for `artifacts`, create the amendment pull request |
| `fix` | the amendment branch `speckit-amend/<folder>` | fix the consistency findings listed in the evidence (step 7) |
| `revise` | the amendment branch `speckit-amend/<folder>` | rework the amendment with a person's feedback listed in the evidence (step 8) |

## Principles

- Spec Kit treats spec → plan (`plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`) → tasks as
  the source of truth, and implementation follows them. When the artifacts are wrong, fix the earliest wrong
  artifact and let implementation redo the work; never patch around an artifact defect in code.
- Many stops are **not** artifact defects. Classify honestly; an amendment is only one of five outcomes.
- Never weaken requirements, success criteria, tests, thresholds, or verification to get past a stop. A requirement
  change (`spec.md`) is a person's decision.
- An amendment leaves the whole spec folder consistent: every artifact that states, depends on, or tests a changed
  decision changes with it. An independent `/speckit-analyze` checks this after you; stale statements are findings.
- Evidence first: state the evidence for and against the conclusion, and how sure you are.
- Respect `.specify/memory/constitution.md` and the architecture in `docs/architecture/`.

## Outline

1. **Gather the evidence.**
   - Non-interactive: read `.speckit-diagnosis/evidence.md` and `.speckit-diagnosis/context.json` (stop reason,
     check runs, recent comments with the failure reports and agent summaries, logs of failed runs, branch state,
     the person's notes, a previous diagnosis, and the section **Commands you can recommend**).
   - Interactive: with `gh`, read the pull request, its comments (the stop comment, the failure comments of the stuck
     task, convergence, or conflict resolution, an earlier diagnosis starting with
     `<!-- speckit-implement:diagnosis -->`), the failed runs of the chain's workflows
     (`gh run list --workflow speckit-implement-group.yml`, likewise `speckit-implement.yml`,
     `speckit-implement-verify.yml`, `speckit-implement-repair.yml`, and `speckit-implement-merge.yml`;
     `gh run view <id> --log-failed`), and an open amendment pull
     request from `speckit-amend/<folder>` with its comments and reviews. Print the command reference with
     `node .github/scripts/speckit-diagnose.mjs commands`. Work in a worktree of the implementation
     branch (`git worktree add <temp-dir> origin/speckit/<folder>`), or of the amendment branch to rework it.
   - **Commands you can recommend** is generated from the commands themselves: their usage, flags, what each does on
     this pull request (including a pending repair), how far their guidance reaches, and when to recommend them. It is
     the authority on what a command does; this skill only says how to choose and word one.
2. **Read the artifacts** in the spec folder (`specs/<folder>/`, or `specs/archive/<folder>/` once the implementation
   archived it right before its merge; called `<spec folder>` below) and the constitution, and the code and tests the stuck task touches.
3. **Find the root cause.** Compare the attempts: the same failure every time points to a systematic cause,
   different failures to an approach or flakiness problem. Check what the task asks against what the artifacts
   decide and what the code does. Experiments are allowed (build, run the relevant tests, scratch edits); revert
   scratch edits. Code changes never leave a diagnosis.
   Compare with earlier diagnoses too: when one named the same cause and its recommended step was taken without
   changing the outcome (the same failure again, and nobody pushed a change in between), say so in the summary and do
   not recommend that step again. Find out why it did not help (for example a precondition it assumed was never met,
   or it asked the automation for something outside its scope) and recommend a step that removes that reason. After
   2 retries in a row that left the implementation branch at the same commit, `/speckit retry`
   is refused while the implementation is stopped, so
   recommend a step that changes something: a push, a new spec (`/speckit author`), or an amendment. Only when new guidance that differs
   materially from what was tried can help, recommend `/speckit retry --force <guidance>` and say in `why` what is
   different this time.
4. **Classify** the stop:

   | Category | When | Typical way forward |
   | --- | --- | --- |
   | `artifacts` | The task cannot be done as written, or doing it as written cannot meet the spec: a missing, ambiguous, or contradictory design decision; a wrong assumption in research; a task too vague to diagnose its failure; a missing prerequisite task; a wrong task order; or a code fix inside this feature needed first (a **corrective task**) | an amendment pull request (step 5) that the person reviews and merges |
   | `retry` | Transient infrastructure (timeouts, rate limits, runner or network failures, Copilot errors, a flaky test with evidence), or the agent took a poor approach although the artifacts are adequate | `/speckit retry <concrete guidance>` |
   | `outside` | Environment or tooling (environment actions, workflows), code or tests owned by another feature on the base branch (the implementation pull request's base), permissions, quotas, external services | code, tests, or environment actions not fixed yet: `/speckit author <what to fix, where, and how>`; the fix is already on the base branch: `/speckit sync`; tooling under `.github/` or a non-code cause: name the action in the title, then `/speckit sync` |
   | `decision` | A trade-off only a person can make: relax a success criterion, change scope, split the spec, accept a risk, abandon | numbered questions with the recommended choice first; `/speckit retry 1. <choice> 2. <choice>` prefilled with the recommended answers as guidance |
   | `unknown` | Not enough evidence | what was checked and ruled out; the workflow starts another diagnosis automatically with these findings as context |

   A merged result that fails although both sides pass alone (a conflict resolution or merge whose verification
   fails because of a clash without a textual conflict: two migrations with the same sequence number, an identifier
   both sides added, implementation code that uses something the base branch renamed, a test the base branch added
   that does not know a setting this implementation made required) is this implementation's to fix, not `outside`.
   The conflict resolution may change this branch's own files, add new files, and, when the merged result needs it,
   make the smallest change to files that only the base branch owns (never deleting them; such changes hold the final
   merge for a person's review). So classify it as `retry` and recommend `/speckit retry <how to integrate>` that names
   the files and the change (for example "renumber this feature's migration `0017_x.sql` to `0019_x.sql` with `git mv`
   and update its references", or "add the `Email:*` settings to `RequiredSettings` in the base branch's
   `MinimumWebVersionTests.cs`"). Never guide toward weakening a test, an assertion, or a requirement on either side;
   a test that races or fails after the merge is fixed by waiting for the right condition, not by changing what it
   expects. It is `outside` only when the base branch fails on its own.

5. **Amend (only for `artifacts`).**
   - Edit the highest wrong artifact first, then carry the change down: `spec.md` (only with a person's answer) →
     `plan.md` (including risks and the documentation of decisions), `research.md`, `data-model.md`, `contracts/`,
     `quickstart.md` → `tasks.md`. Search the whole folder for every statement about the changed decision
     (`grep` for its names, files, and identifiers) and update each one.
   - When `spec.md` changes, also re-check `checklists/requirements.md` against the changed requirements and update
     the items you can verify; never check an item you have not verified.
   - Record a changed decision in `research.md` as a refined decision with its rationale, the rejected alternative,
     and the evidence (for example measurements from the failed attempts).
   - `tasks.md`: keep task IDs; never check a task; do not remove or reword completed tasks; to redo a completed task,
     uncheck it and say why. New tasks get the next free ID (highest + 1) and go where they must run; a corrective
     task goes directly before the stuck task. Mark a task `[P]` only when it touches other files than its neighbours
     and depends on no unfinished task. Reference artifact sections and requirement IDs like the existing tasks, and
     update dependency and strategy notes that mention the changed tasks.
   - Architecture (constitution Principle I): when the amendment changes a design decision that `docs/architecture/`
     states, or that `plan.md` § Architecture Updates lists, update that section and make sure an unchecked task
     carries the change into the owning narrative: extend the unchecked documentation task, or, when it is already
     checked, add a documentation task with the next free ID before the convergence phases. Never change
     `docs/architecture/` itself in an amendment.
   - Re-check consistency like `/speckit-analyze` for the whole folder before you finish.
   - Non-interactive: change only files in `<spec folder>/`, then call `create_pull_request` with branch
     `speckit-amend/<folder>`, base `speckit/<folder>`, a title naming the cause, and a body with the cause and the
     changed artifacts with one line each on what changed and why. Do not commit or push yourself.
   - Interactive: show the diff and ask. Then either commit `docs(<folder>): amend spec artifacts` on the
     implementation branch and push (a person's push restarts the implementation with a fresh attempt count), or
     push it to `speckit-amend/<folder>` and open a pull request against `speckit/<folder>` for review.
6. **Report** the diagnosis.
   - Non-interactive: write the report below to a scratch file outside the repository and check it with
     `node .speckit-tooling/.github/scripts/speckit-diagnose.mjs check-report <file>`, which prints the problems and
     the comment a person will see. Fix the report until it prints "The report is valid.", then call
     `speckit_diagnosis` exactly once with it as one JSON string.
   - Interactive: summarize the cause, evidence, and options; ask which option to take; carry it out, either locally
     or by commenting the command on the pull request (`gh pr comment <n> --body "/speckit retry <guidance>"`).
     `/speckit help` on the pull request lists every command.
7. **Fix consistency findings** (mode `fix`). The evidence lists the findings of the independent analysis (and broken
   amendment rules, if any). Fix each one in the artifacts of `<spec folder>/` on the amendment branch, following
   the rules of step 5; when a finding is wrong, leave the artifacts as they are and say why. Commit
   `docs(<folder>): fix consistency findings` and call `push_to_pull_request_branch` for the amendment pull request.
   Report with the rework report below.
8. **Rework with feedback** (mode `revise`). The evidence lists the person's comments and review comments since the
   last rework. Address each one in the artifacts on the amendment branch, following the rules of step 5; a request
   to change requirements is the person's decision, so follow it. When feedback is a question or asks for something
   an amendment cannot do (for example "just retry"), answer it in `responses` without changing files. Commit
   `docs(<folder>): rework the amendment` and call `push_to_pull_request_branch` for the amendment pull request when
   files changed. Report with the rework report below.

## Diagnosis report

```json
{
  "category": "artifacts | retry | outside | decision | unknown",
  "confidence": "high | medium | low",
  "summary": "two or three sentences a person reads first",
  "cause": "the root cause and the reasoning, in Markdown",
  "evidence": ["one line per piece of evidence, with numbers, files, and runs"],
  "artifacts": [{ "file": "specs/<folder>/research.md", "reason": "what changed and why" }],
  "options": [{ "title": "what the option does, in a few words", "why": "why it solves this problem, from the evidence", "command": "/speckit retry Rerun the verification unchanged; only UploadSessionExpiryTests failed on timing.", "recommended": true }],
  "questions": [{ "question": "a decision a person must make", "choices": ["recommended choice", "other choice"] }]
}
```

- `options` are JSON objects, never plain text. Mark exactly one as recommended: the one that **solves** the problem,
  not the cheapest one. It is the only next step the person sees prominently, so never recommend a retry you expect to
  fail. The person decides from the comment alone: `why` explains in one or two sentences why the option solves this
  problem (the comment adds what the command does).
- The recommended command must fit the category: `retry` → `/speckit retry`; `outside` → `/speckit author`,
  or `/speckit sync`; `decision` → `/speckit retry` with the recommended answers as guidance (the categories each
  command fits are also listed under **Commands you can recommend**).
  `artifacts` must create an amendment pull request, and `unknown` starts another diagnosis automatically.
  Inconsistent reports are rejected at publication, not replaced with a retry. A required fix
  already on the base branch is `outside`, not `retry`; recommend `/speckit sync` so the merge and verification
  are mandatory. Never embed another command in retry guidance as a substitute for selecting that command.
- A `command` is one line a person can post as it is, in the form `/speckit <command> [flags] [guidance]`. Read what
  each command does, its flags, and how far its guidance reaches under **Commands you can recommend**, and choose by
  that, not from memory. Write the guidance or answers out from your findings; never leave
  placeholders such as `<guidance>` or `[notes]`, and leave the text out
  only when there is nothing useful to add. For `decision`, prefill the answers with the recommended choices:
  `/speckit retry 1. <first choice> 2. <first choice>`.
- Guidance reaches only the first work that continues (the reference names it for each command), so write it for that
  work: for example the files and change of a conflict resolution, or the test and condition of a flaky verification.
  Put `--` before guidance that itself starts with `--`, so it is not read as a flag.
- When a repair is pending (the reference says so), `/speckit retry <guidance>` runs a repair agent on the already
  landed code and then reverifies, while `/speckit retry` without guidance only reverifies the failed scope. Add
  guidance only when a code change is needed; for a flaky or transient failure, recommend the bare command and say in
  `why` that the verification runs again unchanged.
- A recommended `/speckit retry` (category `retry`, confidence medium or high) or `/speckit sync` (category `outside`)
  may be posted by the automation itself, once after each action of a person: write its guidance so it is right without
  anyone reading it first, and use `low` confidence when a person should look before it runs again.
- Never recommend a command that already failed the same way. When the evidence has a section **A command that already
  failed the same way**, the last `/speckit retry` or `/speckit sync` left the branch unchanged and stopped with the same
  reason as before; recommending it again only repeats the stop (a recommendation of it is moved to the other options at
  publication). Recommend what changes something instead, for example `/speckit author` for a cause outside this spec.
- Guidance only steers the next agent's prompt; it changes no check, limit, or verification. A stop that happens before
  any agent runs (for example conflicts under `.github/` or `.specify/`) cannot be changed by guidance. Conflicts under
  `specs/` that the archive's reference rewrites cause are resolved by script and never need a person.
- Never recommend that a person resolves something locally and pushes when a command can do it on the server: the
  automation resolves conflicts, repairs code, and authors new specs itself (`/speckit author`).
- When the solution needs an action outside these commands (for example "grant the workflow access to the
  registry"), describe that action in the option's title and set `command` to the command that continues afterwards
  (usually `/speckit retry <guidance>`), so the recommendation is one option. Its guidance must not claim that the
  action is done ("after pushing the rename …", "the file is now renamed"): a person may post the command before
  acting, and the work then fails again the same way. Prefer a command that lets the automation make a code change
  itself (task, repair, or conflict-resolution guidance) over asking a person to push it. Reviewing and merging the
  amendment pull request has `"command": null`. Add other options only when they are real alternatives, each with its
  `why`, and give each the command that does what its title says.
- `artifacts` lists the changed files of the amendment; leave it empty otherwise. Ask at most three questions, each
  with its recommended choice first.

## Rework report (modes `fix` and `revise`)

```json
{
  "summary": "what changed in this round, in one or two sentences",
  "artifacts": [{ "file": "specs/<folder>/plan.md", "reason": "what changed and why" }],
  "responses": [{ "feedback": "short quote of the finding or comment", "response": "what you did, or why not" }]
}
```

## Done When

- [ ] The stop is classified with evidence and a confidence (mode `diagnose`).
- [ ] An amendment changes only `<spec folder>/` and leaves the whole folder consistent.
- [ ] Every finding (mode `fix`) or piece of feedback (mode `revise`) has a response.
- [ ] The report (non-interactive) or the chosen option (interactive) is delivered.

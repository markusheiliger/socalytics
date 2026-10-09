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
     the person's notes, a previous diagnosis).
   - Interactive: with `gh`, read the pull request, its comments (the stop comment, the failure comments of the stuck
     step, an earlier diagnosis starting with `<!-- speckit-implement:diagnosis -->`), the failed runs
     (`gh run list --workflow speckit-implement.yml`, `gh run view <id> --log-failed`), and an open amendment pull
     request from `speckit-amend/<folder>` with its comments and reviews. Work in a worktree of the implementation
     branch (`git worktree add <temp-dir> origin/speckit/<folder>`), or of the amendment branch to rework it.
2. **Read the artifacts** in `specs/<folder>/` and the constitution, and the code and tests the stuck task touches.
3. **Find the root cause.** Compare the attempts: the same failure every time points to a systematic cause,
   different failures to an approach or flakiness problem. Check what the task asks against what the artifacts
   decide and what the code does. Experiments are allowed (build, run the relevant tests, scratch edits); revert
   scratch edits. Code changes never leave a diagnosis.
4. **Classify** the stop:

   | Category | When | Typical way forward |
   | --- | --- | --- |
   | `artifacts` | The task cannot be done as written, or doing it as written cannot meet the spec: a missing, ambiguous, or contradictory design decision; a wrong assumption in research; a task too vague to diagnose its failure; a missing prerequisite task; a wrong task order; or a code fix inside this feature needed first (a **corrective task**) | an amendment pull request (step 5) that the person reviews and merges |
   | `retry` | Transient infrastructure (timeouts, rate limits, runner or network failures, Copilot errors, a flaky test with evidence), or the agent took a poor approach although the artifacts are adequate | `/speckit resume <concrete guidance>` |
   | `outside` | Environment or tooling (environment actions, workflows), code or tests owned by another feature on the default branch, permissions, quotas, external services | name what must change where (a fix on the default branch, an environment spec, a tooling fix); when the fix is already on the default branch, `/speckit sync` |
   | `decision` | A trade-off only a person can make: relax a success criterion, change scope, split the spec, accept a risk, abandon | numbered options with questions; the person answers with `/speckit diagnose <answers>` |
   | `unknown` | Not enough evidence | what was checked and ruled out, and what to look at next |

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
   - Re-check consistency like `/speckit-analyze` for the whole folder before you finish.
   - Non-interactive: change only files in `specs/<folder>/`, then call `create_pull_request` with branch
     `speckit-amend/<folder>`, base `speckit/<folder>`, a title naming the cause, and a body with the cause and the
     changed artifacts with one line each on what changed and why. Do not commit or push yourself.
   - Interactive: show the diff and ask. Then either commit `docs(<folder>): amend spec artifacts` on the
     implementation branch and push (a person's push restarts the implementation with a fresh attempt count), or
     push it to `speckit-amend/<folder>` and open a pull request against `speckit/<folder>` for review.
6. **Report** the diagnosis.
   - Non-interactive: call `speckit_diagnosis` exactly once with the diagnosis report below as one JSON string.
   - Interactive: summarize the cause, evidence, and options; ask which option to take; carry it out, either locally
     or by commenting the command on the pull request (`gh pr comment <n> --body "/speckit resume <guidance>"`).
7. **Fix consistency findings** (mode `fix`). The evidence lists the findings of the independent analysis (and broken
   amendment rules, if any). Fix each one in the artifacts of `specs/<folder>/` on the amendment branch, following
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
  "options": [{ "title": "what the option does and why", "command": "/speckit resume <guidance>", "recommended": true }],
  "questions": [{ "question": "a decision a person must make", "choices": ["choice 1", "choice 2"] }]
}
```

- Mark exactly one option as recommended. A `command` is one line: `/speckit resume <guidance>`, `/speckit sync`, or
  `/speckit diagnose <notes>`. Reviewing and merging the amendment pull request, and actions a person takes outside
  these commands (for example "fix the trigger test on the default branch"), have `"command": null`.
- `artifacts` lists the changed files of the amendment; leave it empty otherwise. Ask at most three questions.

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
- [ ] An amendment changes only `specs/<folder>/` and leaves the whole folder consistent.
- [ ] Every finding (mode `fix`) or piece of feedback (mode `revise`) has a response.
- [ ] The report (non-interactive) or the chosen option (interactive) is delivered.

---
description: Diagnose why a Spec Kit implementation on GitHub stopped and decide how to continue; amend the spec artifacts only when they are the cause.
---

## User Input

```text
$ARGUMENTS
```

The input may name the implementation pull request (`#49` or `49`), the twin issue, or the spec folder, and may add
notes. Without any of them, use the open implementation pull request of the active feature from
`.specify/feature.json`.

**Mode.** Run **non-interactive** when `.speckit-diagnosis/evidence.md` exists (the `Spec Kit diagnose` agentic
workflow started you): never ask, and turn every open question into a report question. Otherwise run
**interactive**: ask the user (one question at a time, with choices) whenever a decision is theirs.

## Principles

- Spec Kit treats spec → plan (`plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md`) → tasks as
  the source of truth, and implementation follows them. When the artifacts are wrong, fix the earliest wrong
  artifact and let implementation redo the work; never patch around an artifact defect in code.
- Many stops are **not** artifact defects. Classify honestly; an amendment is only one of five outcomes.
- Never weaken requirements, success criteria, tests, thresholds, or verification to get past a stop. A requirement
  change (`spec.md`) is a person's decision.
- Evidence first: state the evidence for and against the conclusion, and how sure you are.
- Respect `.specify/memory/constitution.md` and the architecture in `docs/architecture/`.

## Outline

1. **Gather the evidence.**
   - Non-interactive: read `.speckit-diagnosis/evidence.md` and `.speckit-diagnosis/context.json` (stop reason,
     check runs, recent comments with the failure reports and agent summaries, logs of failed runs, branch state,
     the person's notes, a previous diagnosis). The workspace is the implementation branch.
   - Interactive: with `gh`, read the pull request, its comments (the stop comment, the failure comments of the stuck
     step, an earlier diagnosis starting with `<!-- speckit-implement:diagnosis -->`), the failed runs
     (`gh run list --workflow speckit-implement.yml`, `gh run view <id> --log-failed`), and an open amendment pull
     request from `speckit-amend/<folder>`. Work in a worktree of the implementation branch
     (`git worktree add <temp-dir> origin/speckit/<folder>`).
2. **Read the artifacts** in `specs/<folder>/` and the constitution, and the code and tests the stuck task touches.
3. **Find the root cause.** Compare the attempts: the same failure every time points to a systematic cause,
   different failures to an approach or flakiness problem. Check what the task asks against what the artifacts
   decide and what the code does. Experiments are allowed (build, run the relevant tests, scratch edits); revert
   scratch edits. Code changes never leave a diagnosis.
4. **Classify** the stop:

   | Category | When | Typical way forward |
   | --- | --- | --- |
   | `artifacts` | The task cannot be done as written, or doing it as written cannot meet the spec: a missing, ambiguous, or contradictory design decision; a wrong assumption in research; a task too vague to diagnose its failure; a missing prerequisite task; a wrong task order; or a code fix inside this feature needed first (a **corrective task**) | an amendment (step 5), then `/speckit apply` |
   | `retry` | Transient infrastructure (timeouts, rate limits, runner or network failures, Copilot errors, a flaky test with evidence), or the agent took a poor approach although the artifacts are adequate | `/speckit resume <concrete guidance>` |
   | `outside` | Environment or tooling (environment actions, workflows), code or tests owned by another feature on the default branch, permissions, quotas, external services | name what must change where (a fix on the default branch, an environment spec, a tooling fix); when the fix is already on the default branch, `/speckit sync` |
   | `decision` | A trade-off only a person can make: relax a success criterion, change scope, split the spec, accept a risk, abandon | numbered options with questions; a requirement change goes through `/speckit revise <answer>` |
   | `unknown` | Not enough evidence | what was checked and ruled out, and what to look at next |

5. **Amend (only for `artifacts`).**
   - Edit the highest wrong artifact first, then carry the change down: `spec.md` (only with a person's answer) →
     `plan.md`, `research.md`, `data-model.md`, `contracts/`, `quickstart.md` → `tasks.md`.
   - Record a changed decision in `research.md` as a refined decision with its rationale, the rejected alternative,
     and the evidence (for example measurements from the failed attempts).
   - `tasks.md`: keep task IDs; never check a task; do not remove or reword completed tasks; to redo a completed task,
     uncheck it and say why. New tasks get the next free ID (highest + 1) and go where they must run; a corrective
     task goes directly before the stuck task. Mark a task `[P]` only when it touches other files than its neighbours
     and depends on no unfinished task. Reference artifact sections and requirement IDs like the existing tasks.
   - Re-check consistency like `/speckit-analyze` for everything you touched: requirement coverage, terminology,
     task references, and the constitution.
   - Non-interactive: change only files in `specs/<folder>/`, then call `create_pull_request` with branch
     `speckit-amend/<folder>`, base `speckit/<folder>`, a title naming the cause, and a body with the cause, the
     changed artifacts, and the consistency notes. Do not commit or push yourself.
   - Interactive: show the diff and ask. Then either commit `docs(<folder>): amend spec artifacts` on the
     implementation branch and push (a person's push restarts the implementation with a fresh attempt count), or
     push it to `speckit-amend/<folder>` and open a pull request against `speckit/<folder>` for review.
6. **Report.**
   - Non-interactive: call `speckit_diagnosis` exactly once with the report below as one JSON string.
   - Interactive: summarize the cause, evidence, and options; ask which option to take; carry it out, either locally
     or by commenting the command on the pull request (`gh pr comment <n> --body "/speckit resume <guidance>"`).

## Report

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

- Mark exactly one option as recommended. A `command` is one line: `/speckit resume <guidance>`, `/speckit sync`,
  `/speckit apply` (only when you created an amendment pull request), `/speckit revise <notes>`, or
  `/speckit diagnose <notes>`. Actions a person takes outside these commands (for example "fix the trigger test on the
  default branch") have `"command": null`.
- `artifacts` lists the changed files of the amendment; leave it empty otherwise. Ask at most three questions.

## Done When

- [ ] The stop is classified with evidence and a confidence.
- [ ] For `artifacts`, the amendment changes only `specs/<folder>/` and is consistent.
- [ ] The report (non-interactive) or the chosen option (interactive) is delivered.

---
description: Author or rework a fix spec, a small standalone Spec Kit spec for the cause of a stopped implementation that lies outside its spec; it is proposed against the default branch and unblocks the implementation once it has landed.
---

## User Input

```text
$ARGUMENTS
```

The input may name the stopped implementation pull request (`#75` or `75`) and add guidance on what to fix.

**Interaction.** Run **non-interactive** when `.speckit-fix/evidence.md` exists (the `Spec Kit fix` agentic workflow
started you): never ask; when something is unclear, choose the smallest fix that the evidence supports and say so in
the spec's Assumptions. Otherwise run **interactive**: explain that a fix spec is requested by commenting
`/speckit fix <what to fix>` on the implementation pull request, which authors, checks, and links it, and resumes the
implementation once it has landed. Offer to post that comment (`gh pr comment <n> --body "/speckit fix …"`) with
guidance drawn from the latest diagnosis on the pull request; a fix spec written by hand is not linked to the
implementation.

**Mode** (non-interactive: `mode` in `.speckit-fix/context.json`):

| Mode | Workspace | Task |
| --- | --- | --- |
| `author` | the default branch | write a new fix spec and propose it (steps 1–6) |
| `fix` | the fix spec branch `speckit-fix/<folder>` | fix the consistency findings listed in the evidence (step 7) |
| `revise` | the fix spec branch `speckit-fix/<folder>` | rework the fix spec with a person's feedback listed in the evidence (step 8) |

## What a fix spec is

- A standalone Spec Kit spec (spec → plan → tasks) whose only purpose is to remove the cause that stopped another
  implementation, where that cause lives: code or tests of another feature on the default branch, or the
  environment actions (`.github/actions/environment-setup` and `environment-verify`).
- It is implemented, verified, converged, and merged like any spec, so the fix is documented and governed by the
  constitution. The stopped implementation waits for it and retries by itself once it has landed.
- It is minimal: the smallest change that removes the cause. It never weakens requirements, success criteria, tests,
  thresholds, or verification of the feature that owns the code; a flaky test is made deterministic, not skipped or
  loosened.
- When the cause is the environment actions, the fix spec is an **environment spec**: its tasks change nothing but
  the two action folders, and its implementation is always held for a person's review.
- Refuse (author nothing, explain in `refused`) when a fix spec cannot remove the cause: tooling under `.github/`
  other than the environment actions, permissions, quotas, secrets, external services, or a cause that lies in the
  stopped spec itself (that needs an amendment, which the automatic diagnosis proposes).

## Outline

1. **Gather the evidence.** Read `.speckit-fix/evidence.md` (the guidance of the person who asked, the latest
   diagnosis, and recent comments) and `.speckit-fix/context.json`. Read the code and tests the cause involves on the
   default branch, find the feature that owns them (`git log` of the files, `specs/archive/`), and read its spec. Run
   the relevant tests when that helps to confirm the cause; revert scratch edits.
2. **Decide** whether a fix spec can remove the cause (see above). If not, change nothing and go to step 6 with
   `refused`. **One cause gets one fix spec**: the evidence lists the existing fix specs (open for review, or merged and
   still to be implemented). When one of them already removes the cause of this stop, even though another
   implementation asked for it, write nothing and go to step 6 with `reuse` set to its pull request number and a
   `summary` that says why it is the same cause; this implementation then waits for that fix spec and resumes once it
   has landed. Write a new fix spec only for a cause none of them removes.
3. **Write the spec folder** `specs/<folderPrefix><short-name>/` (`folderPrefix` from `context.json`; `short-name`
   two to four lowercase words joined by `-` that name the fix). Use the templates in `.specify/templates/` and the
   Spec Kit conventions of the existing specs:
   - `spec.md`: the title names the fix. Directly below the metadata, add the line `fixForLine` from `context.json`
     exactly as given. Describe the problem with its evidence, one user story, functional requirements, measurable
     success criteria (the stopped verification passes, the fixed test passes reliably), and under Assumptions →
     Dependencies the feature that owns the code (its spec folder) and the implementation the fix unblocks. No
     `[NEEDS CLARIFICATION]` markers.
   - `plan.md`: technical context, Constitution Check, project structure (only the files the fix changes), and the
     `## Architecture Updates` section: `None` with a reason when the fix changes no design decision, else the
     narratives and statements it changes.
   - `tasks.md`: phases and tasks `T001`… in the format of the existing `tasks.md` files, each with the exact files it
     changes and how it is verified; a documentation task that references `docs/architecture/` when the plan lists
     architecture updates.
   - `checklists/requirements.md`: the requirements quality checklist of `/speckit-specify`. Verify every item and
     check it; an item that does not hold means the spec must change until it does.
   - `specs/README.md`: add the folder's row like the existing rows.
4. **Check it** from the repository root with
   `node .speckit-tooling/.github/scripts/speckit-author.mjs status <folder>` until it reports "Publish blockers: none",
   and re-check consistency like `/speckit-analyze` for the whole folder. An independent analysis checks it again.
5. **Propose it**: change only `specs/<folder>/` and `specs/README.md`, then call `create_pull_request` with branch
   `speckit-fix/<folder>`, a title naming the fix (the prefix "Fix spec: " is added), and a body with the cause, the
   feature that owns the code, what the fix changes, and which implementation it unblocks. Do not commit or push
   yourself.
6. **Report**: call `speckit_fix` exactly once with the author report below.
7. **Fix consistency findings** (mode `fix`). The evidence lists the findings of the independent analysis (and broken
   fix spec rules, if any). Fix each one in `specs/<folder>/` (and its row in `specs/README.md`) on the fix spec branch,
   following steps 3 and 4; when a finding is wrong, leave the artifacts as they are and say why. Commit
   `docs(<folder>): fix consistency findings` and call `push_to_pull_request_branch` for the fix spec pull request.
   Report with the rework report below.
8. **Rework with feedback** (mode `revise`). The evidence lists the person's comments and review comments since the
   last rework. Address each one following steps 3 and 4; when feedback is a question or asks for something a fix spec
   cannot do, answer it in `responses` without changing files. Commit `docs(<folder>): rework the fix spec` and call
   `push_to_pull_request_branch` for the fix spec pull request when files changed. Report with the rework report below.

## Author report (mode `author`)

```json
{
  "folder": "the new spec folder name, empty when refused or reused",
  "summary": "what the fix spec changes and why it removes the cause, or why the reused fix spec removes it, in one or two sentences",
  "refused": "why no fix spec was written; empty when one was proposed or reused",
  "reuse": "the pull request number of the existing fix spec that already removes this cause; empty otherwise"
}
```

## Rework report (modes `fix` and `revise`)

```json
{
  "summary": "what changed in this round, in one or two sentences",
  "artifacts": [{ "file": "specs/<folder>/plan.md", "reason": "what changed and why" }],
  "responses": [{ "feedback": "short quote of the finding or comment", "response": "what you did, or why not" }]
}
```

## Done When

- [ ] The fix spec removes the cause where it lives, minimally, without weakening anything (mode `author`), an existing
      fix spec that already removes it is reused instead of writing a second one, or the refusal says why a fix spec
      cannot remove it.
- [ ] `speckit-author.mjs status` reports no publish blockers, and the folder is consistent.
- [ ] Every finding (mode `fix`) or piece of feedback (mode `revise`) has a response.
- [ ] The report is delivered.

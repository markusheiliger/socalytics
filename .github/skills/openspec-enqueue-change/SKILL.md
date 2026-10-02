---
name: openspec-enqueue-change
description: Enqueue one or more committed OpenSpec changes through their GitHub issue twins. Use when the user invokes /opsx-enqueue, invokes this skill directly, or asks to enqueue, queue, or batch-process OpenSpec changes.
license: MIT
compatibility: Requires the openspec, git, and gh CLIs and an authenticated GitHub CLI session.
metadata:
  author: SocAlytics
  version: "1.2"
---

Enqueue one or more OpenSpec changes by applying the existing
`openspec:enqueued` label to their GitHub issue twins.

This is a client-side repository tooling workflow. Do not invoke any script
under `.github/scripts/`, create OpenSpec planning artifacts, modify native
issue dependencies, dispatch Agent Tasks, or assign issues to Copilot.

## Input

Accept zero or more exact kebab-case OpenSpec change refs supplied with the
operation.

- With refs, validate exactly those initial selections.
- Without refs, discover every eligible change and use the complete eligible
  set as the initial selection. Do not ask the user to select changes
  individually.
- Users who want a subset must supply its exact refs with the operation.

## Safety boundary

All discovery, validation, dependency expansion, and confirmation steps are
read-only. The only permitted mutation is adding `openspec:enqueued` to the
confirmed issue twins after the final preflight.

Never:

- print or request authentication tokens;
- interpolate an unvalidated ref into a shell command;
- create or repair issue twins or labels;
- add or remove native issue dependencies;
- remove queue labels;
- infer queue state from issue prose outside the managed markers and labels;
- claim that a multi-issue label operation is atomic.

Stop on malformed JSON, malformed or duplicate managed markers, ambiguous
issue twins, an unexpected default branch, an unavailable CLI, failed
authentication, or a failed read request.

## Phase 1: Resolve repository context

1. Run `openspec context --json`. Use `root.path` as the OpenSpec root. Stop if
   no root resolves.
2. Run `git rev-parse --show-toplevel` and require the same repository root.
3. Run `gh repo view --json nameWithOwner,defaultBranchRef` and require the
   default branch name to be `main`.
4. Verify `gh auth status` succeeds.
5. Run `git fetch --no-tags origin main` before evaluating pushed state.
6. Run `openspec list --json` and require its `root.path` to match the resolved
   OpenSpec root.
7. Validate every supplied ref with
   `^[a-z0-9]+(?:-[a-z0-9]+)*$` and require it to appear exactly once in the
   active change list.

Use argument arrays or safely quoted literal arguments for every CLI call.
Never construct executable shell text from a ref.

## Phase 2: Build the issue-twin inventory

Read all issues carrying `openspec:change`, including closed issues, with
`gh issue list --state all --label openspec:change --limit 1000` and JSON fields
for number, state, body, labels, title, and URL.

Each issue body must contain exactly one `<!-- openspec-json ... -->` marker
whose JSON has
`"$schema": ".github/scripts/schemas/change-marker-v1.schema.json"`.
Parse its JSON and accept only the fields and invariants already used by
the queue:

- `repository` equals the resolved `nameWithOwner`;
- `ref` is a kebab-case stable change ref;
- `lifecycle` is `active` or `archived`;
- `gitRef` is non-empty;
- an active path is exactly `openspec/changes/<ref>`;
- an archived path matches
  `openspec/changes/archive/YYYY-MM-DD-<ref>`.

Fail closed if two issue twins have the same ref or a marker targets another
repository.

## Phase 3: Determine admission eligibility

Evaluate every supplied ref, or every active ref when building the
no-argument selection. A change is eligible for initial admission only when all
checks pass:

1. `openspec validate <ref> --type change --strict --json --no-interactive`
   reports exactly one valid item and zero failures.
2. `git ls-files -- openspec/changes/<ref>` returns at least one tracked path.
3. `git status --porcelain=v1 --untracked-files=all --
   openspec/changes/<ref>` returns no entry.
4. `git diff --quiet origin/main -- openspec/changes/<ref>` succeeds.
5. `git log -1 --format=%H -- openspec/changes/<ref>` returns a full commit SHA,
   and `git merge-base --is-ancestor <sha> origin/main` succeeds.
6. Exactly one issue twin has the ref, is open, and has an active marker with
   `gitRef: main` and path `openspec/changes/<ref>`.
7. The issue has none of these labels, which mean a request or an open
   processing pull request already exists:
   - `openspec:enqueued`;
   - `openspec:processing`;
   - `openspec:awaiting-review`.
8. If the issue has `openspec:needs-attention` without `openspec:processing`,
   its previous pull request was closed without merging. Such a change is
   eligible only when its ref was supplied explicitly; never add it to the
   no-argument selection.

Keep a reason for every rejected change. Supplied refs fail the operation when ineligible. The no-argument selection
contains all eligible changes, including changes that currently have native
blockers.

If the no-argument candidate set is empty, stop without showing an empty
question and summarize the rejection reasons.

## Phase 4: Select changes

When refs were supplied, use them as the initial selection.

Otherwise, use every eligible change as the initial selection. Selection is
automatic at this phase; the user reviews the dependency-complete ordered set
and explicitly confirms it in Phase 6.

## Phase 5: Compute dependency closure

For each selected issue, query the native dependency endpoint with pagination:

`gh api --paginate repos/{owner}/{repo}/issues/{issue_number}/dependencies/blocked_by`

Walk blockers transitively and detect cycles. Native GitHub relationships are
the only dependency authority.

Classify each blocker:

- **Satisfied**: the blocker is an OpenSpec issue twin whose issue is closed,
  whose marker lifecycle is `archived`, and whose exact archived path exists on
  `main` according to the GitHub contents API.
- **Already admitted**: the blocker is an open active OpenSpec twin carrying
  `openspec:enqueued`, `openspec:processing`, or `openspec:awaiting-review`.
  It does not need to join the selection.
- **Addable**: the blocker is an open active OpenSpec twin that passes every
  admission eligibility check.
- **Blocking error**: the blocker is not an OpenSpec twin, has a malformed
  marker, was stopped (`openspec:needs-attention` without
  `openspec:processing`), or fails eligibility.

If any blocking error or cycle exists, stop without mutation and report the
complete blocking path.

If addable transitive blockers are missing from the selection, show the entire
missing set and ask one single-choice question with exactly:

- `Add all required blockers and continue`
- `Cancel`

On acceptance, add the complete missing transitive set. On cancellation, stop
without mutation. Recompute closure until the selection is complete; never ask
the user to add blockers one at a time.

## Phase 6: Confirm and revalidate

Topologically order the dependency-complete selection so every selected
blocker precedes its selected dependents. Show the final ordered refs and issue
numbers.

Ask for explicit confirmation with exactly:

- `Enqueue these changes`
- `Cancel`

On cancellation, stop without mutation.

After confirmation, repeat Phases 1 through 5 for the final set. Do not add
newly discovered eligible changes during revalidation. Allow an issue that
gained `openspec:enqueued` during revalidation as an idempotent success, but
otherwise stop before the first write if repository state, issue identity,
eligibility, or dependency closure changed.

## Phase 7: Enqueue

Process the topological order. Before each write, read the issue labels again.

- If `openspec:enqueued` is already present, record `already enqueued`.
- Otherwise add only that label through the issue-label API, for example:
  `gh api --method POST repos/{owner}/{repo}/issues/{issue_number}/labels
  -f "labels[]=openspec:enqueued"`.
- If a label write fails, record the error and skip every selected descendant
  that depends on the failed issue. Continue independent branches.
- Do not roll back successful labels; the openspec workflow may already have
  consumed them.

Report a table containing change ref, issue number, and one of:

- `enqueued`;
- `already enqueued`;
- `failed`;
- `skipped because blocker failed`.

Report full success only when every selected issue is enqueued or already
enqueued. For partial failure, state that GitHub label mutations are not atomic
and that rerunning the operation is safe because labeling is idempotent.

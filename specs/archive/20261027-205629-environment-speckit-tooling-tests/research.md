# Research: Spec Kit Tooling Tests in Environment Verification

No `NEEDS CLARIFICATION` remains. Each decision below was checked against `.github/actions/environment-verify/` as it exists on the base branch.

## 1. Which tests run, and where from

- **Decision**: Run `node --test` over `.github/scripts/speckit-*.test.mjs` of the verified workspace (the step already runs from the workspace root), expanded by Bash with `nullglob` so an empty match is detected, not passed on literally.
- **Rationale**: The check verifies the branch's tooling, so it must run the branch's copy; the Markdown check also runs against the workspace while the helper scripts come from the action path. The scripts' own tests locate sibling modules relative to themselves.
- **Alternatives considered**: Test files next to the action (`${{ github.action_path }}`), like `ENVIRONMENT_TESTS`; rejected because the action path may be the trusted tooling checkout, which would test the wrong copy.

## 2. How the new check stays out of existing fixtures

- **Decision**: Gate the step on a new environment variable `SPECKIT_TOOLING_TESTS` (the tooling folder, `.github/scripts`) that `action.yml` sets and fixtures leave unset, as `ENVIRONMENT_TESTS` works today.
- **Rationale**: Existing finalize fixtures have no `.github/scripts/` folder; without a gate every one would fail with "no test file" and "All existing tests pass unchanged" (SC-005) would not hold.
- **Alternatives considered**: Skip silently when the folder is missing (violates the edge case "no test file fails"); edit every existing fixture (large, risky).

## 3. How a task-named file starts the check

- **Decision**: Rely on the change list. The unchanged-branch verification (`verifiedUnchangedCheck` in `speckit-implement-work.mjs`) requires `unchanged.files` to equal the task-named files, which means they reach the action as its change list; matching the change list therefore covers "changed file or task-named file".
- **Rationale**: No new input, no change outside the two action folders, and the planned outcome for T004 and T005 of the stopped spec follows from coverage plus a check other than "Markdown check".
- **Alternatives considered**: A new action input for task files (changes the action interface, FR-012); extending `verifiedUnchangedCheck` (out of scope).

## 4. Trigger pattern

- **Decision**: `^\.github/scripts/speckit-[^/]+\.mjs$`, used for the trigger and the covered scope, and kept equal in `action.yml` and `verification-scope.mjs` by a self-test that reads both.
- **Rationale**: Matches `speckit-*.mjs` and `speckit-*.test.mjs` directly under `.github/scripts/` (the clarified trigger, deletions included because the change list names deleted paths), and nothing else: no `check-markdown.mjs`, no workflow, skill, or other action, no subfolder.
- **Alternatives considered**: Everything under `.github/scripts/` (covers helpers the tests do not exercise, violates FR-007); two patterns (redundant, since the test pattern is a subset).

## 5. Scope reason text

- **Decision**: `classify` reports "`<file>` is a Spec Kit tooling file covered by the Spec Kit tooling tests" for a tooling file and keeps "`<file>` is not Markdown" for any other file; the result stays `documentationOnly: false`.
- **Rationale**: FR-008 asks only that the misleading wording disappears for covered files; the full-verification decision is unchanged.
- **Alternatives considered**: A new classification state (changes the result shape, FR-012).

## 6. Failure message and nested-run guard

- **Decision**: One line naming the check, the reproduce command, and one recommendation, like "Environment action tests"; the step runs under `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1` and is skipped when that marker is already set.
- **Rationale**: FR-002 and FR-004; the self-test fixtures already rely on the marker, so verification never recurses.
- **Alternatives considered**: A separate marker (two markers would let one check start inside the other for no benefit).

## 7. Node.js availability

- **Decision**: Fail with a message naming `node` when `command -v node` fails, before any test starts.
- **Rationale**: Edge case: no silent pass; the other Node-based steps rely on the caller's Node.js and the plan itself needs it.
- **Alternatives considered**: Installing Node (forbidden by FR-001).

## 8. Tooling tests need no network

- **Decision**: The assumption that `node --test .github/scripts/speckit-*.test.mjs` needs no network, credentials, or services is evidenced by the implementation's final run of `node --test .github/scripts/speckit-*.test.mjs`, which is recorded as passing without installing anything or using credentials (the closing steps of the README task); network isolation is not claimed.
- **Rationale**: FR-001 forbids installing anything, so the check can only rely on what a clean checkout provides.
- **Alternatives considered**: None.

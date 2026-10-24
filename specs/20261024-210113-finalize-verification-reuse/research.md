# Research: Finalize Verification Without Repeated Work

All Technical Context items are resolved; the decisions below fix the open design points of the spec.

## R1 - Where the logic lives

- **Decision**: Inline bash and `node -e` snippets inside the `Run the checks` step of
  `.github/actions/environment-verify/action.yml`, in the style of `trx_failed` and `playwright_flaky`.
- **Rationale**: FR-010 confines the change to the action folder and its three existing files; the workflows that call
  the action are protected, so no new input is possible. Node is already required by the action (`sdk_pin`,
  `trx_failed`), so JSON and hashing need no new tool.
- **Alternatives considered**: a new helper script next to the action (adds a file the spec does not list; rejected);
  a new input or workflow step (protected workflows; rejected).

## R2 - Identifying "the same job" and where the record lives

- **Decision**: One JSON file `finalize-<GITHUB_RUN_ID>-<GITHUB_RUN_ATTEMPT>-<GITHUB_JOB>.json` in
  `$RUNNER_TEMP/socalytics-environment-verify/`. If `RUNNER_TEMP` or any of the three values is empty, nothing is
  written or read. Each name part is reduced to `[A-Za-z0-9_.-]` (any other character becomes `_`) so the file name
  cannot leave the folder.
- **Rationale**: Clarified in the spec (session 2026-10-24). GitHub-hosted runners give every job its own clean
  `RUNNER_TEMP`, and the three values separate attempts and jobs even on a shared self-hosted runner. A matrix leg
  shares `GITHUB_JOB` but not the runner.
- **Alternatives considered**: a record in the workspace or an artifact (outlives the job or changes the workspace
  identity; forbidden by FR-001).

## R3 - Workspace state identity

- **Decision**: `identity = sha256(HEAD commit + "\n" + tree)` where `tree` is `git write-tree` of a throw-away index
  (`GIT_INDEX_FILE` in a private temporary folder) filled with `git read-tree HEAD` and
  `git add -A -- . ':(exclude)<path>'` for the caller's `log-file` and `changed-files` when they lie inside the
  workspace. The real index and the working tree are never touched.
  - The identity is **uncertain** (no record written, none reused, every check of the scope runs, and the run reports
    that reuse was not possible; FR-014) when: `git` fails or the workspace is not the top level of a Git work tree,
    or an **ignored** path exists that is not a known generated or dependency folder.
  - Ignored paths are listed with `git ls-files --others --ignored --exclude-standard --directory`. A path is accepted
    only when a path component is one of `bin`, `obj`, `node_modules`, `.venv`, `venv`, `__pycache__`, `dist`, `build`,
    `TestResults`, `test-results`, `playwright-report`, `.vs`, `.pytest_cache`, or `.uv-cache`, or the path ends with `src/clients/web/src/generated` (the Web UI build's generated client, matched as a path suffix, never as a bare component `generated`), or the path ends with `.specify/workflows/runs` (the Spec Kit run folder of the `resolve` workflow that precedes the verification of a conflict resolution: `.specify/.gitignore` ignores it, the composite action removes the run folder after collecting it, but the empty parent folder can remain and is then listed; matched as a path suffix), or the path ends with `.specify/feature.json`, `.specify/.workflow-install.lock`, or `.specify/extensions/<name>/local-config.yml` (Spec Kit housekeeping that `.specify/.gitignore` lists; tooling state outside the build, matched as path suffixes); any other ignored file (for example
    an ignored configuration or source file) makes the identity uncertain because it could affect the build without
    being part of the tree.
  - The identity is computed at the start (`S`) and again after the checks (`E`); a result is recorded only when
    `S == E`, so checks that modify tracked or unignored files (for example generated sources) never produce a record
    that the next run's start identity could falsely match or that hides a changed workspace.
- **Rationale**: A tree hash covers every committed and uncommitted content difference, mode bits, additions, and
  deletions (spec: "even by a single file"). Ignored build outputs (`bin`, `obj`, `node_modules`) always exist after a
  first run and must not defeat reuse in the real scenario; allowing only known generated folders keeps the "certain
  or nothing" rule without making reuse unreachable.
- **Alternatives considered**: `git status` text (does not hash content; rejected); hashing the whole directory
  (includes `bin/obj`, minutes of work, and nondeterministic outputs; rejected); treating any ignored file as
  uncertain (would disable reuse after the first build; rejected).

## R4 - Changed-files content

- **Decision**: Strip `\r`, drop blank lines, sort unique, join with `\n`, `sha256`. The same normalized list also
  drives the scope decision.
- **Rationale**: Edge case "same files, different order, line endings, or blank lines" counts as identical; a
  different set never does.

## R5 - Environment-only scope

- **Decision**: Scope `environment` when the normalized list is non-empty and **every** path matches
  `^\.github/actions/environment-(setup|verify)/[^/]` and has no `.`/`..` component, no backslash, no leading `/`, and
  no empty component; otherwise scope `full`. Only `finalize` mode reduces. In the reduced scope the platform,
  Analyst Manager, Web UI, and Analyst Python checks are skipped (the Python helper is not run, so a malformed
  Analyst project cannot fail an environment-only change), and the Environment action tests and the Markdown check
  run in the existing order.
- **Rationale**: Clarified in the spec (only the environment tests and the Markdown check). The complete-component
  regex already rules out `environment-setup-x/` lookalikes; the explicit `..` rule rules out
  `.github/actions/environment-setup/../../x`. Every changed path in the reduced scope is in `COVERED`, so `uncovered`
  stays empty and no coverage is claimed for a path whose check did not run (FR-008).
- **Alternatives considered**: keeping the Agent Python projects check in the reduced scope (rejected by the
  clarification; it is the expensive part this spec removes for nothing).

## R6 - Reuse rule and failure handling

- **Decision**: At the start of a `finalize` run with a certain identity, a record is reused only when it parses, has
  `version: 1`, `passed: true`, string `identity`, `mode`, `changedFiles`, `scope`, `checks`, `uncovered`, `flaky`,
  and a `source` object, and `identity`, `mode`, `changedFiles`, and `scope` equal the run's own. Otherwise every check
  of the scope runs. A run that fails (or is cancelled) never writes a record; a run that fails also deletes an
  existing record, so a later run can never inherit an earlier pass over a failure. A run that passes overwrites the
  record atomically (temporary file in the same folder, then `mv`).
- **Reused run output**: the same `checks`, `uncovered`, and `flaky` outputs as the record, a log block and step
  summary section titled "Reused result" naming the earlier run (`source`: run id, attempt, job, record time, number of
  the finalize run in the job), exit 0.
- **Nested runs**: when `SOCALYTICS_ENVIRONMENT_TESTS_RUNNING` is set (the action's own tests), the script never reads
  or writes a record, so fixtures cannot interact with the job's real record.
- **Residual risk**: the self-test of the synchronized workspace copy of the action is satisfied by the earlier trusted
  result, so it does not execute a changed `environment-verify` script or its tests; the environment action tests of the
  first finalize run (which cover both action folders) are the evidence for the synchronized copy.
- **Rationale**: Every read failure falls through to a full run (FR-004); there is no input that turns a failure into a
  pass.

## R7 - Time effect

- **Decision**: Nothing to tune. The second run's cost is the identity computation (seconds) plus reading the record;
  the reduced scope removes the platform, Web UI, Manager, and Python checks from an environment-only change. SC-001
  and SC-002 are verified by the tests' count of executed stub commands (the reused run executes none) and by the
  next real conflict resolution; the 76-minute figure is observed, not simulated.

## R8 - Test approach

- **Decision**: Extend the `verification execution` block of `analyst-projects.test.mjs`. Its `run` helper already
  executes the extracted action script against stubbed `uv`; the new tests turn the fixture repository into a Git
  repository, pass `RUNNER_TEMP`, `GITHUB_RUN_ID`, `GITHUB_RUN_ATTEMPT`, and `GITHUB_JOB`, and count the stub `uv`
  calls as the expensive work. Existing tests set none of these values, so they run unchanged without records.

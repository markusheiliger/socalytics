# Research: Sample Tooling Tests in Environment Verification

No `NEEDS CLARIFICATION` remains. Findings come from `.github/actions/environment-verify/` (`action.yml`,
`verification-plan.mjs`, `verification-scope.mjs`, their tests, and the README).

## Decision 1: Model the check on "Spec Kit tooling tests", not on a component

- **Decision**: A Bash block in `action.yml` gated by `SAMPLE_TOOLING_TESTS` (workspace-relative `tools/samples.test.mjs`,
  unset in fixtures), `MAIN=1`, the nested-run marker, and `applies <regex>`.
- **Rationale**: Components are discovered under `src/`; `tools/` is not a source area and the constitution forbids
  adding children of `src/`. The Spec Kit tooling check already solves the same problem (repository tooling tested by
  Node, first shard only, nested guard, fixture isolation, failure message).
- **Alternatives**: a `verification.json` component under `tools/` (declarations are limited to `src/` components and a
  declaration change would hold every pull request for review); a broader `tools/` glob (rejected by FR-005).

## Decision 2: Exact path pattern, no glob

- **Decision**: One anchored, case-sensitive regular expression for the four files, used for selection (`applies`), for
  coverage (`verification-plan.mjs`), and for the scope reason (`verification-scope.mjs`); the Bash pattern and the
  JavaScript pattern are kept equal by a test that reads the action text.
- **Rationale**: Suffixes (`.bak`), nested paths, and case variants must stay uncovered (US2 scenario 3).
- **Alternatives**: `tools/*.mjs` (covers `tools/other.mjs`, rejected).

## Decision 3: Coverage independent of mode, selection dependent on mode

- **Decision**: The four paths are always counted covered (like `TOOLING_FILE`); the check runs per `applies`.
- **Rationale**: In every mode where one of the four files changed the check is selected, so coverage always has a
  running test behind it. A documentation-only decision never applies to these paths because they are not Markdown.

## Decision 4: Sharding and budget

- **Decision**: Shared check of the first shard; raise `MAIN_MINUTES` by one minute and extend the shard log text.
- **Rationale**: `packShards` already treats shared checks as one fixed budget; a separate unit would need a new
  shard kind and could run twice. Tests import `MAIN_MINUTES`, so they follow the constant.
- **Alternatives**: a separate bin unit (more complexity, no benefit for a seconds-long test).

## Decision 5: Failure semantics

- **Decision**: Missing `node`, a missing `tools/samples.test.mjs`, and a failing test each set `status=1` after the
  check name is added to `checks`; each prints one `Recommended action`.
- **Rationale**: FR-003 and FR-004; a deleted test file with another of the four files changed must not pass silently.

## Decision 6: No change to environment-setup

- **Decision**: Leave `environment-setup` unchanged; the check uses the Node.js that the other Node checks use.
- **Rationale**: The tests need no network, credentials, or services (spec assumption). Spec scope allows but does not
  require a setup change.

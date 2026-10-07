# Implementation Plan: Environment Verification Coverage

**Branch**: `20261007-115855-environment-verification-coverage` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261007-115855-environment-verification-coverage/spec.md`

## Summary

Extend the two composite actions that the Spec Kit workflows use for setup and
verification so that (1) changes in the repository-root `contracts/` folder are
covered by the existing platform restore, build, and test check, and (2) the
Analyst Manager solution `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`
gets its own restore, build, and test check. Both coverages activate only once
their project exists, keep today's behavior otherwise, and add no new tool:
the Manager pins the same SDK as the platform, which `environment-setup`
already installs. See [research.md](research.md) for the decisions.

## Technical Context

**Language/Version**: Bash inside GitHub composite actions (YAML); the checks
invoke the .NET 10 SDK pinned by `src/platform/global.json` (`10.0.400`,
`rollForward: latestPatch`).

**Primary Dependencies**: `actions/setup-dotnet@v5` (already used), `dotnet`
CLI, Node.js for the existing Markdown check. No new dependency.

**Storage**: N/A

**Testing**: The merge workflow's self-test of a branch's own environment
actions, plus the validation scenarios in [quickstart.md](quickstart.md), run
in Bash against a scratch copy of the repository.

**Target Platform**: GitHub-hosted `ubuntu-latest` runners used by the Spec Kit
workflows.

**Project Type**: CI extension point (composite actions).

**Performance Goals**: No additional runtime for platform-only or
Markdown-only changes; Manager check runs only when its scope changed or in
finalize mode.

**Constraints**: Change only `.github/actions/environment-setup/` and
`.github/actions/environment-verify/` (exclusivity check); keep inputs,
outputs, check names, order, and uncovered reporting unchanged for existing
scopes; no secrets or tokens in the verify job.

**Scale/Scope**: Two YAML files; one new check block, one extended scope.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle / rule | Pre-design | Post-design | Evidence |
| --- | --- | --- | --- |
| I. Architecture is the design authority | PASS | PASS | Repository-root `contracts/` is the location planned by `docs/architecture/contracts-and-compatibility.md`; the Manager location follows `src/analysts/README.md`. No architecture change needed. |
| II. Source-area ownership | PASS | PASS | No source files added; only the environment extension points change. |
| III. API-first control plane | N/A | N/A | No product behavior. |
| IV. Evidence over claims | PASS | PASS | Coverage is proven by the merge self-test and the quickstart scenarios; skipped checks never report success for code that exists. |
| V. Focused, minimal changes | PASS | PASS | Contracts reuse the platform check; the Manager reuses the installed SDK; no new tool. |
| Technology: environment features | PASS | PASS | This *is* the environment feature for Recording Lineage and Upload, Durable Analysis Workflow, and Analyst Manager Registration; it changes only the two action folders and is held for human review by design. |
| Workflow: documentation | PASS | PASS | The action descriptions document the coverage (FR-009); README updates follow when dependent features create the covered projects (spec Assumptions). |

No violations; Complexity Tracking is not needed.

## Project Structure

### Documentation (this feature)

```text
specs/20261007-115855-environment-verification-coverage/
├── plan.md              # This file
├── research.md          # Decisions
├── data-model.md        # Verification scopes and checks
├── quickstart.md        # Validation scenarios
├── contracts/
│   └── verify-coverage.md  # Coverage contract of environment-verify
└── tasks.md             # Created by /speckit-tasks
```

### Source Code (repository root)

```text
.github/actions/
├── environment-setup/
│   └── action.yml       # Description notes that the Manager reuses the platform SDK pin
└── environment-verify/
    └── action.yml       # contracts/ joins the platform scope; new Manager check block
```

**Structure Decision**: Only the two composite actions change, as the
constitution's environment-feature rule requires.

## Design

`environment-verify` (see [contracts/verify-coverage.md](contracts/verify-coverage.md)):

1. Platform scope becomes `^(src/platform|contracts)/`, used for both
   `COVERED` (only when the platform solution exists) and `applies`, so a
   change touching both areas runs the platform check once (FR-001, FR-002).
2. New variable `MANAGER_SOLUTION=src/analysts/manager/SocAlytics.Analysts.Manager.slnx`.
   When it exists, `^src/analysts/manager/` joins `COVERED` (FR-003).
3. New block after the platform check and before the Markdown check: when
   `status` is 0, the scope applies, and the solution exists, append the check
   name `Analyst Manager build and tests` and run, from
   `src/analysts/manager`, `dotnet restore`, `dotnet build --no-restore`, and
   `dotnet test --no-build` on the solution; any failure sets `status=1`
   (FR-004). While the solution is missing the block is skipped (FR-005).
4. Before restoring, the Manager block compares the `sdk.version` of
   `src/analysts/manager/global.json` with `src/platform/global.json` and
   fails the check with a clear message when they differ (FR-006), so the
   setup step never needs a second SDK install.
5. Comments in both action files describe the new coverage and its
   activation rule (FR-009). Existing check names, order (platform, Manager,
   Markdown), outputs, and uncovered reporting stay unchanged (FR-008).

`environment-setup`: no step changes; only the description gains the
statement that the Analyst Manager pins the same SDK version (FR-006, FR-007).

## Complexity Tracking

Not applicable.

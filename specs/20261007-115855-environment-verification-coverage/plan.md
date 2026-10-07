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
already installs, and the only new tool is the SoftHSM2 software PKCS#11 token
that the Manager's Linux key-provider tests use. See [research.md](research.md) for the decisions.

## Technical Context

**Language/Version**: Bash inside GitHub composite actions (YAML); the checks
invoke the .NET 10 SDK pinned by `src/platform/global.json` (`10.0.400`,
`rollForward: latestPatch`).

**Primary Dependencies**: `actions/setup-dotnet@v5` (already used), `dotnet`
CLI, Node.js for the existing Markdown check, and the Ubuntu package
`softhsm2` (new, installed with `apt-get`).

**Storage**: N/A

**Testing**: The validation scenarios in [quickstart.md](quickstart.md), run
by the final task (T007) in Bash against a scratch copy of the repository
outside the working tree, prove contract and Manager coverage (SC-001,
SC-003); T007 reports each scenario's expected and actual outcome in its final
summary for the reviewer. The merge workflow's self-test of the branch's own
environment actions proves only the unchanged baseline (SC-002), because
neither `contracts/` nor the Manager solution exists when this feature merges.

**Target Platform**: GitHub-hosted `ubuntu-latest` runners used by the Spec Kit
workflows.

**Project Type**: CI extension point (composite actions).

**Performance Goals**: No additional runtime for platform-only or
Markdown-only changes; Manager check runs only when its trigger
(`src/analysts/manager/` or `contracts/analyst-manager/`) changed or in
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
| IV. Evidence over claims | PASS | PASS | The merge self-test proves only the unchanged baseline (SC-002); contract and Manager coverage (SC-001, SC-003) are proven by the quickstart scenarios that T007 runs and reports per scenario in its final summary for the reviewer; skipped checks never report success for code that exists. |
| V. Focused, minimal changes | PASS | PASS | Contracts reuse the platform check; the Manager reuses the installed SDK; the only new tool is SoftHSM2, required by the Manager's PKCS#11 provider tests (spec FR-006). |
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
│   └── action.yml       # Installs softhsm2; notes that the Manager reuses the platform SDK pin
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
2. New variables `MANAGER_SOLUTION=src/analysts/manager/SocAlytics.Analysts.Manager.slnx`,
   `MANAGER_SCOPE='^src/analysts/manager/'`, and
   `MANAGER_TRIGGER='^(src/analysts/manager|contracts/analyst-manager)/'`.
   When the solution exists, only `MANAGER_SCOPE` joins `COVERED` (FR-003);
   `contracts/` files stay covered only by the platform scope (FR-001).
3. New block after the platform check and before the Markdown check: when
   `status` is 0, `applies "$MANAGER_TRIGGER"` holds, and the solution exists,
   append the check name `Analyst Manager build and tests` and run, from
   `src/analysts/manager`, `dotnet restore`, `dotnet build --no-restore`, and
   `dotnet test --no-build` on the solution; any failure sets `status=1`
   (FR-004). The trigger includes `contracts/analyst-manager/` because the
   Manager tests read the shared golden fixtures there (research.md § R3a);
   such a change runs the platform check first and then the Manager check.
   While the solution is missing the block is skipped (FR-005).
4. Before entering `src/analysts/manager` (while the working directory is
   still the workspace root), the Manager block compares the complete `sdk`
   objects (`version`, `rollForward`, `allowPrerelease`) of
   `src/analysts/manager/global.json` and `src/platform/global.json`, and fails
   the check with a message naming both values, without restoring, when they
   differ or the Manager file is missing (FR-006), so the setup step never
   needs a second SDK install.
5. Comments in both action files describe the new coverage and its
   activation rule (FR-009). Existing check names, order (platform, Manager,
   Markdown), outputs, and uncovered reporting stay unchanged (FR-008).

`environment-setup`: a new step installs the Ubuntu package `softhsm2`
(`sudo apt-get update && sudo apt-get install --yes softhsm2`) so the Manager's
PKCS#11 key-provider tests find `libsofthsm2.so` and `softhsm2-util`; each
test run creates its own token directory through `SOFTHSM2_CONF`, so the setup
creates no token. The description also states that the Analyst Manager pins the
same SDK version (FR-006, FR-007).

## Risk Register

| ID | Risk | Disposition | Evidence / Owner | Revisit trigger |
| --- | --- | --- | --- | --- |
| ENV-R1 | A contract change runs the full platform build and tests, which is slower than a contract-only check | Accepted | One scope keeps the platform check single-run (FR-002); contract tests live in the platform solution (R1) | Contract-only runs become a bottleneck |
| ENV-R2 | The Manager pins a different SDK than the platform | Mitigated | The Manager check fails fast when the `global.json` `sdk` objects differ (Design step 4, R4) | A second SDK is genuinely needed |
| ENV-R5 | A change to the Manager's shared golden fixtures under `contracts/analyst-manager/` does not run the Manager tests | Mitigated | `MANAGER_TRIGGER` includes `contracts/analyst-manager/` (Design steps 2–3, R3a) | The Manager reads fixtures from another path |
| ENV-R3 | `apt-get` install of `softhsm2` fails or slows setup | Accepted | Standard Ubuntu package used by the Manager spike on the runner image (R4a) | Setup failures on runner image updates |
| ENV-R4 | Windows CNG and macOS Secure Enclave providers are not verified by the environment | Accepted | Verified manually per the Manager quickstart (R5) | A Windows or macOS CI job is added |

## Complexity Tracking

Not applicable.

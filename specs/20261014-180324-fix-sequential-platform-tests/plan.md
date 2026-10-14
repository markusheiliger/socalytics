# Implementation Plan: Sequential Platform Test Projects in Environment Verification

**Branch**: `20261014-180324-fix-sequential-platform-tests` | **Date**: 2026-10-14 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261014-180324-fix-sequential-platform-tests/spec.md`

## Summary

In phase and finalize mode, `run_tests` of `environment-verify` runs each `*Tests.csproj` of the solution in its own `dotnet test <project> --no-build` call, one after another, instead of one solution-wide call, so Docker-backed test projects never start containers at the same time.

## Technical Context

**Language/Version**: Bash in a composite GitHub Action (`.github/actions/environment-verify/action.yml`).

**Primary Dependencies**: existing `dotnet` SDK; no new tools.

**Testing**: shell-level check of the loop with a throwaway solution folder containing stub test projects is not required; verify by reading the log of a finalize run (`node .github/scripts/speckit-*.test.mjs` if the tooling tests cover the action) and by the merge self-test of the changed action.

**Constraints**: environment spec: only the two action folders change. Reuse the project discovery of task mode (`find ... -name '*Tests.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort`). Keep going after a failed project and fail the check at the end. Task mode is unchanged.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only the environment action. | Pass |
| III. API-first control plane | Unaffected. | Pass |
| IV. Evidence over claims | Verified by the merge self-test and the unblocked verification. | Pass |
| V. Focused, minimal changes | One function in one file. | Pass |

## Project Structure

```text
specs/20261014-180324-fix-sequential-platform-tests/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

.github/actions/environment-verify/
└── action.yml
```

## Architecture Updates

None. The change affects only the verification action and alters no design decision or architecture statement.

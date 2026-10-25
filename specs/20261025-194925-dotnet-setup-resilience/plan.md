# Implementation Plan: Resilient .NET Environment Setup

**Branch**: `20261025-194925-dotnet-setup-resilience` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-194925-dotnet-setup-resilience/spec.md`

## Summary

The first step of `.github/actions/environment-setup`, `actions/setup-dotnet@v5` (a floating tag), has no retry and no fallback, so a persistent failure of the primary .NET download origin fails every work job before any agent runs. The plan replaces it with a bounded, unrolled sequence of three pinned `setup-dotnet` attempts with pauses (composite actions have no loops), followed by one decision step, a Node helper `dotnet-resolve.mjs`, that (1) accepts the primary result only if it satisfies both pinned SDK files, (2) otherwise falls back, in a documented order, to an SDK already on the runner and then to an alternative official download feed, (3) validates every candidate against `src/platform/global.json` and `src/analysts/manager/global.json` with a pure decision function plus a cross-check by the real `dotnet` muxer, (4) publishes the resolved version as the existing `dotnet-version` step output (the step keeps the id `dotnet`, so the cache key step is unchanged), (5) writes a run summary naming the source, fallback use, and attempts, and (6) fails with one message naming every source tried and one recommended action. Offline tests in the style of `toolchain.test.mjs` cover the decision logic. Only the two environment action folders change.

## Technical Context

**Language/Version**: Node.js ES modules (the calling workflows provide Node.js 24) for the helper and its tests; GitHub composite action YAML; Bash for the alternative-feed install

**Primary Dependencies**: `actions/setup-dotnet` (third-party, pinned to a full commit, see research R1); `dotnet-install.sh` from the official .NET install-scripts feed for the alternative source (R3); Node built-ins only, no npm dependency

**Storage**: N/A (files in `$RUNNER_TEMP` only; no cache is added)

**Testing**: `node --test` with `node:test`, offline, fixtures and simulated source outcomes; the existing `toolchain.test.mjs` and `analyst-projects.test.mjs` keep passing

**Target Platform**: GitHub-hosted Linux x64 runners (the Spec Kit workflow runners)

**Project Type**: CI composite action (environment extension point, no product code)

**Performance Goals**: Primary success path adds no download and negligible overhead (one helper run of well under a second plus two `dotnet --version` calls)

**Constraints**: Development limits (R4): at most 3 `setup-dotnet` attempts of 3 minutes each with pauses of 10 s and 30 s, then the runner SDK check (seconds) and one alternative-feed install of at most 4 minutes: a hard outage fails the run in about 14 minutes at the very worst and in about 2 minutes for fast-failing responses such as HTTP 409. No secrets, no new permissions, no new credentials, cloud-neutral

**Scale/Scope**: One action, one new helper script, one new test file, README section, one list entry in `environment-verify`

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design: unchanged, all pass.*

| Principle or constraint | Result |
| --- | --- |
| I. Architecture Is the Design Authority | Pass. No architecture narrative describes the setup action internals; see Architecture Updates. |
| II. Respect Source-Area Ownership | Pass. Nothing under `src/` changes and no new first-level folder. |
| III. API-First Control Plane | Not applicable (no product capability). |
| IV. Evidence Over Claims | Pass. The decision logic has offline automated tests; no production readiness is claimed, limits are labelled development values. |
| V. Focused, Minimal Changes | Pass. One helper, no dependency, no change to `install-tools` semantics, no tool added or removed. |
| Environment constraint (setup/verify actions) | Pass. This is the standalone environment spec; only `.github/actions/environment-setup/` and, strictly needed, one list entry in `.github/actions/environment-verify/action.yml` change. No other spec needs new tooling from it. |
| Spec Kit authority rules (`AGENTS.md`) | Pass. No workflow, script under `.github/scripts/`, spec folder, or `.specify/` file is touched. |

No violations; Complexity Tracking is empty.

## Architecture Updates

None. The feature changes only the repository's CI environment action (a build-environment extension point owned by `.github/actions/`), not the platform, clients, agents, analysts, contracts, or any system design. `docs/architecture/` describes no part of the setup action; a search for the action and its .NET setup step found no architecture statement that this change makes true or false, and the documentation task needs no `docs/architecture/` change. The action's own [README](../../.github/actions/environment-setup/README.md) is the owning documentation and is updated by the implementation (FR-014).

## Project Structure

### Documentation (this feature)

```text
specs/20261025-194925-dotnet-setup-resilience/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/
│   └── dotnet-resolve-interface.md  # Phase 1 output (helper and action interface)
├── checklists/
│   └── requirements.md
└── tasks.md             # Phase 2 output (/speckit-tasks)
```

### Source Code (repository root)

```text
.github/actions/environment-setup/
├── action.yml               # changed: pinned setup-dotnet attempts, pauses, resolve step (id dotnet)
├── dotnet-resolve.mjs       # new: pure decision logic, source adapters, CLI for the resolve step
├── dotnet-resolve.test.mjs  # new: offline tests of the decision logic
├── toolchain.test.mjs       # changed: expected step list and action.yml shape assertions for the new .NET steps
└── README.md                # changed: .NET section (limits, order, acceptance rule, reporting, recovery)

.github/actions/environment-verify/
└── action.yml               # changed only: ENVIRONMENT_TESTS also lists ../environment-setup/dotnet-resolve.test.mjs
```

**Structure Decision**: Keep the helper beside `cache-keys.mjs` in the setup action folder, exactly as `cache-keys.mjs` and `install-analyst-media.sh` are kept, so no new directory is created. The `environment-verify` change is the single line that makes verification run the new offline tests (otherwise they would never run; adding a test to the list removes or relaxes no check).

## Complexity Tracking

No constitution violations to justify.

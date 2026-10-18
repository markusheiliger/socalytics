# Implementation Plan: Analyst and Media Verification Environment

**Branch**: `main` (authoring) | **Feature**: `20261017-163801-analyst-media-environment` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Standalone environment prerequisite for the approved SDK, media-input, fact-query, and analysis-review tranche.

## Summary

Extend only the two environment action folders. Install a hash-verified, pinned CPU toolchain for Python Analyst projects and synthetic media tests, then add project-aware locked package verification without broadening unrelated coverage. Keep existing action interfaces, trusted caches, platform checks, Manager checks, Web UI checks, and Markdown behavior unchanged.

No product project, workflow, schema, service, image publication, or production support is introduced.

## Technical Context

**Language/Version**: Existing composite-action YAML and Bash; Python 3.12.15 for the new TOML-aware project selector; Node.js 24 built-in tests, already supplied by callers.

**Primary Dependencies**: CPython 3.12.15 from python-build-standalone release `20261013`; `uv` 0.13.1; FFmpeg and ffprobe from the retained monthly BtbN LGPL 8.1 build. Exact artifact identities, digests, and provenance are in [research.md](research.md).

**Storage**: Runner-local installed tools, isolated fixture directories, project dependency environments, and build output only. No database, canonical contract, or additional published dependency cache.

**Testing**: Node's built-in test runner for environment-local selection and execution fixtures; real installed-tool version and synthetic-media checks on the supported Linux runner; existing merge-time environment self-verification.

**Target Platform**: The existing `ubuntu-latest` Linux x64 automated jobs. Check OS and architecture explicitly; unsupported hosts fail with guidance. No Windows, macOS, ARM, GPU, or production compatibility claim.

**Project Type**: Build and verification environment extension, not a product application or SDK.

**Performance Goals**: Check each selected Python project once; check only affected Python projects in task and phase mode; check all existing Python projects in finalize mode. Synthetic smoke media is two seconds at 64 by 64 pixels, not a match-processing benchmark.

**Constraints**: Implementation may change only the two environment action folders and its own task markers. No mutable version selectors, unchecked archive extraction, Python auto-downloads during verification, lockfile regeneration, silent fallback, empty-test success, or new workflow/cache infrastructure.

**Scale/Scope**: One future SDK and independently locked capability projects. Shared Python workspaces and arbitrary nested product layouts are not introduced.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| Architecture authority | Pass: implements the adopted Python, locked-project, and media-tooling direction without claiming product implementation | Pass: exact CI pins remain environment-local; runtime design and production decisions stay unchanged |
| Source ownership | Pass: no new source area or product directory | Pass: future project roots stay inside the existing Analysts ownership area; fixtures stay in the action folders or isolated temporary storage |
| API-first and contract authority | Pass: no product API or data-plane behavior changes | Pass: shared contract changes trigger consumer checks; platform contract validation retains canonical ownership |
| Evidence over claims | Pass: installation and fixture evidence required | Pass: citation research is explicitly not execution evidence; real setup, failure fixtures, and self-verification remain implementation acceptance gates |
| Focused changes | Pass: standalone environment feature | Pass: implementation allow-list is exactly the two action folders; no external workflow or trusted cache change is needed |
| Tooling prerequisite | Pass: this is the prerequisite environment feature itself | Pass: uses existing Bash, Node, and container facilities to provision the new tools; downstream product features must depend on its merge |

No gate exception is needed.

## Architecture Updates

None: this standalone environment feature changes only the two environment action folders. Its pins, fixture checks, and project-verification convention are environment-local executable guidance; they introduce no product component, topology, protocol, canonical contract, or production profile.

The architecture review checked [Analyst Technology](../../../docs/architecture/platform-implementation.md#analyst-technology), [Analyst Containers](../../../docs/architecture/analysts-models-and-hardware.md#analyst-containers), [Analytics](../../../docs/architecture/tenancy-and-technology.md#analytics), [Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md), and [On-Demand Video Segmentation](../../../docs/architecture/match-data-pipeline.md#on-demand-video-segmentation). The existing adopted Python/`uv` direction is unchanged. The statement that the internal Analyst SDK is planned remains true, as do the planned Python transport-client and real media-processing boundaries.

No architecture status statement becomes false when tooling alone merges. Do not mark SDK, capability, Segment Service, client generation, or production behavior implemented. No architecture file changes or `docs/architecture/` documentation task are required; environment-local documentation remains inside the permitted action folders. Downstream product plans must independently adopt their actual source layouts and correct their own implementation-status statements.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261017-163801-analyst-media-environment/
  spec.md
  plan.md
  research.md
  data-model.md
  quickstart.md
  contracts/environment-interface.md
  checklists/requirements.md
```

The tasks skill creates `tasks.md` later. The contract above documents the existing action interface and future project expectations; it is not a new canonical product schema.

### Implementation Allow-List

```text
.github/actions/environment-setup/
  action.yml                         existing action; invoke new installation within install-tools gate
  cache-keys.mjs                     existing; preserve its contract and cache behavior
  analyst-media-toolchain.json       exact versions, archive URLs, SHA-256, expected executable paths
  install-analyst-media.sh           bounded verified installation and synthetic smoke check
  toolchain.test.mjs                 integrity, version, mode, and installation-failure fixtures
  README.md                         environment-local tooling and recovery guide
.github/actions/environment-verify/
  action.yml                         existing action; add project selection and locked verification
  analyst-projects.py                standard-library project/dependency selection
  analyst-projects.test.mjs           selection, command, failure, and reporting fixtures
  README.md                         project contract, coverage, and failure guidance
```

**Structure Decision**: Keep the extension, tests, and documentation inside the two permitted folders. Do not change Spec Kit scripts, workflows, root source, product READMEs, or root contract artifacts.

## Design Decisions

### Verified tool installation

Store all new pins in one environment-local JSON file. Download exact archives into an isolated runner-temporary directory, verify each against its committed SHA-256 before extraction, and install only into action-owned tool directories. Reject archive traversal and unexpected executable layouts. Add the installed binaries to the job path and expose `SOCALYTICS_PYTHON` through the existing job environment mechanism, without adding action outputs.

Assert the exact interpreter and uv versions and the full selected FFmpeg/ffprobe build revision. Preserve distribution license/provenance material beside the installed tools. The media build is LGPL, not the GPL/x264 variant; it is third-party CI tooling, not an approved production redistribution.

Use the synthetic `testsrc2` source and native MPEG-4 encoding for a two-second MP4; extract a nonempty keyframe-compatible subinterval, probe a finite positive duration and video stream, and decode it successfully. No H.264 encoder, external video, model, or GPU is required. Clean up the fixture. Respect `install-tools: false` before any new executable download or smoke check.

### Product project boundary

Recognize independent Python projects at `src/analysts/sdk/pyproject.toml` and `src/analysts/capabilities/<name>/pyproject.toml`, discovered only when they exist. No directories are created by this feature. Each project provides its own `uv.lock`, Python compatibility declaration, package build, locked build backend and pytest dependencies, and test suite.

Use the project's manifest name as its dependency identity, normalized consistently. A local Python dependency is declared through a path source under `[tool.uv.sources]` and a corresponding project dependency. Resolve it to another recognized project inside the workspace. Reject malformed manifests, duplicate identities, unsupported workspace declarations, missing local projects, path escapes, and dependency cycles with corrective guidance.

A Python product manifest elsewhere under the Analysts area, outside the Manager and known ignored/generated directories, is unsupported: report it explicitly and leave its source uncovered. This prevents an unrecognized future layout from silently passing finalize verification.

### Selection, execution, and coverage

Use the existing changed-files input and verification mode. Own-source changes select their existing project; reverse local-dependency closure selects consumers. Changes to `contracts/analysis/`, `contracts/common/`, or either environment action folder select all existing Python projects. Finalize selects every existing Python project.

For each selected project, in deterministic dependency order, use the installed interpreter with Python downloads disabled. First synchronize locked external dependencies and development groups without installing local projects or building external source distributions. Then synchronize local packages without build isolation, build one wheel using that same locked environment, and run pytest without synchronization. The consumer locks the build backends required by itself and its local dependencies. This avoids an unlocked isolated build during the initial sync as well as during the explicit package build. See the exact command contract in [environment-interface.md](contracts/environment-interface.md).

Add only recognized existing project scopes to `COVERED`; contract files remain covered through the existing platform scope. Deleted manifests and unowned Analyst files remain uncovered. Preserve the existing sequential platform tests and existing fail-fast/check-output semantics. A failed Python stage never becomes a successful check or suppresses required output fields.

### Failure guidance and tests

Every new failure names its tool or project, its stage, and exactly one recommended action. Installation failures point to the failed download/integrity/version evidence and environment pin correction where required; project failures point to the exact failed project command and source correction. Never recommend disabling integrity, unlocking verification, weakening tests, or merging without verification.

Environment-local tests create isolated minimal fixture workspaces and stub tool commands; they test action integration as well as pure selection. They must not execute fixture setup in a developer database, write into product source, or depend on real Analyst projects. Add their execution to the verification action for environment changes and finalize mode, including when no Python project exists. Preserve the established caller-facing outputs.

Implementation acceptance also runs real setup and smoke checks on Linux and the repository's existing environment-feature self-verification. Exact commands and the full scenario matrix are in [quickstart.md](quickstart.md). Local documentation-only checks are not substitutes for those later gates.

## Dependencies And Downstream Handoff

- All three implemented environment prerequisites listed in the spec remain authoritative; no open product twin blocks this environment feature.
- The SDK feature must use the declared independent-project and command contract or amend this environment spec before implementation.
- The media feature consumes the installed extraction/probe tools but still owns service design, source-video authorization, segment identity, codec coverage, and runtime evidence.
- The existing real-container feature remains a dependency of later container execution evidence, not this environment setup.
- Pin updates and unsupported toolchains require a subsequent standalone environment change. No automatic tool or profile promotion is added.

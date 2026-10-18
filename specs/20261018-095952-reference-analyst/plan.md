# Implementation Plan: Reference Analyst Container on the Python SDK

**Branch**: `20261018-095952-reference-analyst` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261018-095952-reference-analyst/spec.md`

## Summary

Add the development reference capability `reference-media-probe` as the independent locked Python project
`src/analysts/capabilities/reference-media-probe/`, the first Analyst built on the Analyst SDK. It decodes its staged
segment media with the environment's pinned ffprobe (`movie` + `signalstats`, streamed) and writes one
`frame-second-probe` fact per covered match-second of the nominal window with the decoded-frame count and frame luma
statistics, deterministically, through the SDK. Its two-stage Dockerfile builds from the repository root with only
pinned inputs (base image by digest, `uv.lock`, the environment's hash-verified uv and FFmpeg archives), literal
`io.socalytics.*` labels verified against `analyst-manifest.json` during the build, and runs under any non-root user on
a read-only root. Capability pytest tests prove the facts and failure exits against synthetic media; a Docker-backed
end-to-end test builds, pushes, registers, and runs it on materialized segment media and reads the indexed facts
through the member fact read. No contract, API, persistence, Registry, Manager, or SDK change (research R1–R13).

## Technical Context

**Language/Version**: Python 3.12 (CPython 3.12.15 in verification; the base image's CPython 3.12 in the image) for
the capability; C# on .NET 10 for the end-to-end test.

**Primary Dependencies**: The Analyst SDK `socalytics-analyst-sdk` by local path; `hatchling`, `pytest`, and
`jsonschema` (payload and manifest schema tests) in the locked `dev` group; exact versions in
`src/analysts/capabilities/reference-media-probe/uv.lock`. Image: `python:3.12-slim-bookworm` pinned by digest, uv
0.13.1 and FFmpeg/ffprobe `n8.1.3-9-g29e619e767` LGPL from the environment's toolchain pins. End-to-end: existing
test dependencies only (Testcontainers, xUnit v3, Shouldly).

**Storage**: Files in the container mounts only; no database, migration, object-storage, or OpenAPI change.

**Testing**: pytest in the capability project (environment's locked verification sequence); xUnit v3 Docker-backed
end-to-end test in `SocAlytics.Platform.EndToEnd.Tests`.

**Target Platform**: `linux/amd64` OCI image run by the Analyst Manager's Docker runtime profile; the capability
tests run wherever the environment's toolchain runs (Linux in verification).

**Project Type**: Analyst capability (Python project, OCI image) plus end-to-end test support.

**Performance Goals**: Streaming decode with memory bounded by one frame; the synthetic end-to-end run finishes within
the existing end-to-end timeouts. No throughput target (no catalog gates).

**Constraints**: No network, environment variables, credentials, model, or home directory at run time; read-only root;
any non-root uid; classic-builder-compatible Dockerfile; no change to `.github/`, contracts, platform, Manager, or SDK
code; the configured Registry seed stays unchanged.

**Scale/Scope**: One capability project, one image, one end-to-end test class with its image support.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| I. Architecture authority | Pass: implements the packaging convention and the SDK-based container path; the reference capability is new to the architecture | Pass: design updates applied now to Image Metadata And Packaging and the catalog's rules, worded as decided (see Architecture Updates) |
| II. Source-area ownership | Pass: `src/analysts/capabilities/reference-media-probe/` in the Analysts area; test support in `src/platform/Tests` | Pass: no new first-level area; the `capabilities/` directory starts with its first meaningful artifact |
| III. API-first, contract authority | Pass: uses shared contracts unchanged; the payload schema is component-local | Pass: no data through the control plane; media reaches the Analyst only through Manager staging |
| IV. Evidence over claims | Pass: capability tests and end-to-end evidence required | Pass: no publication, production, or catalog claim; byte-identical rebuilds stay open |
| V. Focused changes | Pass: no platform, Manager, SDK, seed, or AppHost change | Pass: standard-library build helper only; no imaging or numeric library |
| Environment prerequisite | Pass: [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) provides CPython 3.12.15, uv 0.13.1, FFmpeg/ffprobe, and locked verification of `src/analysts/capabilities/<name>/`; Docker and .NET are already provided | Pass: all Python dependencies are locked wheels (`hatchling`, `pytest`, and the SDK's); the image build needs only Docker, which the end-to-end tests already use |

No violation; Complexity Tracking is empty.

## Architecture Updates

**Narratives**:

- [Analysts, Models, and Hardware → Analyst Containers](../../docs/architecture/analysts-models-and-hardware.md#analyst-containers)
  and [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging):
  the first SDK-based Analyst image and the packaging convention it follows.
- [Analysts, Models, and Hardware → Implementation Validation](../../docs/architecture/analysts-models-and-hardware.md#implementation-validation):
  real-container evidence with real segment media.
- [Analyst Capability Catalog → Catalog Rules](../../docs/architecture/analyst-capability-catalog.md#catalog-rules):
  development reference capabilities are not catalog entries.
- [Analyst Runtime and Recovery → Built-In Runtime Adapter Profiles](../../docs/architecture/analyst-runtime-and-recovery.md#built-in-runtime-adapter-profiles)
  and [Runtime Preflight And Status](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status):
  the Docker Desktop profile's end-to-end verification and the container interface a real Analyst uses.
- [Platform Implementation Profile → Source And Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline),
  [Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology),
  [Acceptance Evidence](../../docs/architecture/platform-implementation.md#acceptance-evidence), and
  [Architecture Reassessment](../../docs/architecture/platform-implementation.md#architecture-reassessment): capability
  implementations, locked images, and image reproducibility evidence.

**Design updates applied now** (worded as decided, not yet implemented; the documentation task turns them into present
tense):

- `analysts-models-and-hardware.md` (Image Metadata And Packaging): new paragraph defining `reference-media-probe`, its
  project, facts, no-model digest, pinned image inputs, literal verified labels, non-root read-only run, and its
  development-only status outside the catalog and GHCR.
- `analyst-capability-catalog.md` (Catalog Rules): new paragraph stating that development reference capabilities, the
  first being `reference-media-probe`, are not catalog entries and never enter production workflows.

**Status statements at merge** (each must become true for the merged state):

- `analysts-models-and-hardware.md`, Image Metadata And Packaging: "A development reference Analyst is decided as
  follows and not yet implemented" → implemented as development evidence.
- `analyst-capability-catalog.md`, Catalog Rules: "`reference-media-probe` (decided, not yet implemented …)" → exists.
- `analysts-models-and-hardware.md`, Implementation Validation: the Docker Desktop adapter evidence list → also names
  an SDK-built Analyst decoding Manager-staged segment media whose facts are accepted and indexed; "validate presigned
  video access" stays open.
- `analyst-runtime-and-recovery.md`, Built-In Runtime Adapter Profiles: "The Docker Desktop profile … is implemented and
  verified end to end" → the end-to-end verification includes the reference Analyst on materialized segment media.
- `platform-implementation.md`, Source And Runtime Baseline: "and the future Analyst SDK and Analyst capability
  implementations across their required runtimes" → the development reference capability exists under
  `src/analysts/capabilities/reference-media-probe`; catalog capabilities remain future.
- `platform-implementation.md`, Acceptance Evidence: the Docker-backed end-to-end bullet ("run a test Analyst image
  through the real Analyst Manager core …") → also the SDK-built reference image on materialized segment media with
  accepted, indexed, member-readable facts.
- `platform-implementation.md`, Architecture Reassessment: "reproducible Analyst images … remain unsupported by
  implementation evidence" and "locked Analyst images rebuild reproducibly and emit valid manifests" → the reference
  image evidences locked, pinned-input builds whose manifest the Registry accepts; byte-identical rebuilds and
  production Analyst images remain unevidenced.
- Any other statement found at implementation time that says no Analyst capability implementation or real Analyst image
  built on the SDK exists.

The documentation task MUST make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time; the rewrites above are examples, not the whole
job. It also updates `src/analysts/README.md`, `README.md` (the capability's commands), and `AGENTS.md` (Current State
and commands).

## Project Structure

### Documentation (this feature)

```text
specs/20261018-095952-reference-analyst/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── reference-media-probe.md
├── checklists/requirements.md
└── tasks.md                 # /speckit-tasks
```

### Source Code (repository root)

```text
src/analysts/capabilities/reference-media-probe/       # new independent project
├── pyproject.toml, uv.lock, README.md, .gitignore
├── analyst-manifest.json
├── Dockerfile, Dockerfile.dockerignore
├── docker/fetch_verified.py                          # standard-library download, SHA-256 check, extraction
├── schemas/frame-second-probe.payload.schema.json
├── src/reference_media_probe/
│   ├── __init__.py, __main__.py                      # SDK run(...) entry point
│   ├── probe.py                                      # ffprobe invocation and CSV streaming
│   └── facts.py                                      # per-second aggregation, rounding, fact building
└── tests/
    ├── conftest.py, support/media.py, support/mounts.py   # synthetic media and mount roots
    ├── test_package.py, test_support.py, test_payload_schema.py
    ├── test_probe.py, test_facts.py, test_time_mapping.py, test_determinism.py, test_failures.py
    ├── test_packaging.py                             # manifest schema and package constants
    └── test_dockerfile.py                            # pins vs toolchain JSON, labels, user, final stage, last layer

src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── ReferenceAnalystEndToEndTests.cs
├── ReferenceAnalystImageTests.cs                     # registration, image inspection, restricted run
└── Support/ReferenceAnalystImage.cs                  # temporary context, build, push (as TestAnalystImage)

src/analysts/README.md, README.md, AGENTS.md          # documentation
docs/architecture/                                    # see Architecture Updates
```

**Structure Decision**: The capability is a new independent project at the path the environment feature recognizes;
it depends on `src/analysts/sdk/` through a path source. End-to-end support stays in the existing end-to-end test
project and reuses its harness, registry fixture, and Docker engine helper, and the materialized-segments feature's
synthetic media support.

## Complexity Tracking

No violations.

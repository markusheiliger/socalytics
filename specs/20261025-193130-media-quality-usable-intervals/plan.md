# Implementation Plan: Media Quality and Usable Intervals for Media Context Classification

**Branch**: `20261025-193130-media-quality-usable-intervals` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-193130-media-quality-usable-intervals/spec.md`

## Summary

Extend the development capability `media-context-classification` to version `1.1.0` (analyst manifest `1.2.0`, output
schema `1.1.0`). One decode pass feeds the unchanged cut detector and a new streaming quality analyzer that measures
fixed integer-exact rules on the 64 x 36 frame (black by mean luma, frozen by luma difference to the predecessor,
low detail by mean absolute Laplacian). Finished stretches become `media-quality` facts, and a deterministic sweep over
the stretches and shot boundaries yields `usable-interval` facts that partition the segment's window. All three fact
kinds declare payload schemas in the manifest (the shot-boundary schema is rewritten into the platform's restricted
subset without changing the emitted facts). The image, audit, Docker-backed evidence, README, and architecture status
statements are updated. No platform, SDK, Web UI, or OpenAPI change. See [research.md](research.md).

## Technical Context

**Language/Version**: Python 3.12 (project managed with `uv`)

**Primary Dependencies**: Analyst SDK `socalytics_analyst` (unchanged), `opencv-python-headless`, `numpy` (both already
locked; no new dependency); pinned FFmpeg 8.1 toolchain

**Storage**: N/A (facts travel in the result artifact; no persistence change)

**Testing**: pytest with `jsonschema` and test-time synthetic clips (project); xUnit v3 Docker-backed
`ShotBoundaryImageTests` and `ShotBoundaryEndToEndTests` in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`

**Target Platform**: `linux/amd64` OCI image, non-root, read-only root file system, no network

**Project Type**: Analyst capability (container job, library plus entry point)

**Performance Goals**: evidence segment within 30 s on CPU; one-minute 1080p segment within the manifest's `2Gi` and
the 300 s minimum execution timeout (SC-007)

**Constraints**: byte-identical output across hosts and CPU limits; integer-exact measures; no partial results;
payload schemas in the restricted subset (16 KiB, depth 8, 256 nodes, no `if`, `pattern`, `$schema`, multi-type)

**Scale/Scope**: one segment per job (about 5 s in the evidence, up to minutes in production-like use)

## Constitution Check

*GATE: passed before Phase 0 and re-checked after Phase 1.*

- **I. Architecture authority**: conforms to the Analyst Capability Catalog and the Analysts, Models, and Hardware
  narratives (fixed-rule baseline, restricted payload schemas, development evidence); the statements the feature makes
  false are listed under Architecture Updates. PASS.
- **II. Source-area ownership**: all code stays in `src/analysts/capabilities/media-context-classification`; the
  evidence lives in the existing EndToEnd test project. No new `src/` child. PASS.
- **III. API-first**: no endpoint, contract, or Web UI change; facts use the existing envelope and reads. PASS.
- **IV. Evidence over claims**: project tests, audit, image and end-to-end tests; everything stays labelled
  development evidence with uncalibrated settings. PASS.
- **V. Focused changes**: no new dependency, no SDK or platform change, reference capability untouched. PASS.
- **Environment**: the tasks need only the locked Python libraries, `uv`, the pinned FFmpeg toolchain, and Docker that
  `environment-setup` and `environment-verify` already provide (dependency on the computer-vision environment spec
  stays as stated in the spec); no environment change is needed. PASS.

## Architecture Updates

### Narratives

- [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md): Low-Level Capabilities (the
  `media-context-classification` row and the statement of implemented outputs) and Implementation Waves (Wave 1).
- [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md): the development image
  description (second development image), the fact payload schemas statement of the result manifest section, and the
  Docker-backed verification statement.
- [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md): the Docker-backed
  verification statement naming the shot-boundary baseline.
- [Platform Implementation](../../docs/architecture/platform-implementation.md): the statement of which capabilities
  exist under `src/analysts/capabilities`.

### Design updates

None. The feature adds no component, technology, topology, protocol, or contract to the architecture: it extends an
existing development capability with fixed-rule outputs and uses the implemented fact payload schema declaration. The
fact kinds `media-quality` and `usable-interval` and their rules are component-local contracts documented in the
capability's README, not architecture decisions.

### Status statements at merge

- Analyst Capability Catalog, Low-Level Capabilities introduction: "exists as a shot-boundary baseline only" and
  "Shot type, live, replay, graphics, quality, and usable intervals, and the challenger, are not implemented". It must
  say that the baseline also writes fixed-rule `media-quality` and `usable-interval` facts (version `1.1.0`,
  development evidence, uncalibrated, no gate met), and that shot type, live, replay, graphics, and the challenger are
  not implemented.
- Analyst Capability Catalog, capability table row `media-context-classification` and the sentence after the table
  ("only the shot-boundary baseline ... Its shot types, live, replay, graphics, quality, and usable-interval outputs
  ... are not implemented"): quality and usable intervals become implemented as development evidence; the remaining
  outputs stay not implemented.
- Analyst Capability Catalog, Implementation Waves, Wave 1: "Wave 1 has started only with the shot-boundary baseline
  of media context ... quality, usable intervals ... not implemented". It must name the fixed-rule quality and
  usable-interval outputs as started development evidence and keep every gate stated as not met.
- Analysts, Models, and Hardware, development image paragraph: "`media-context-classification` 1.0.0 ... a
  shot-boundary baseline only". It must describe version `1.1.0` with the three fact kinds, and that `1.0.0` stays
  registrable.
- Analysts, Models, and Hardware, fact payload schemas statement: "no catalog capability declares payload schemas in
  its manifest (the development baseline `media-context-classification` keeps its payload schema in its project, not
  in its `1.0.0` manifest)". It must say that the `1.1.0` development capability declares the payload schemas of all
  three kinds in its `1.2.0` manifest, while no catalog capability does.
- Analysts, Models, and Hardware, Result Manifest And Lineage statements: not made false (the capability declares no
  upstream dependency and changes neither the result manifest nor the lineage shape); no correction is required.
- Analysts, Models, and Hardware, Docker Desktop adapter verification: "the shot-boundary baseline
  `media-context-classification`" and Analyst Runtime and Recovery: "the shot-boundary baseline
  `media-context-classification`, ... the baseline meets no catalog quality gate": the capability is no longer a
  shot-boundary baseline only.
- Platform Implementation, source layout statement: "a shot-boundary baseline only ... (including shot type, live,
  replay, graphics, quality, and usable intervals of media context) ... remain future". Quality and usable intervals
  leave the not-implemented list.
- Outside `docs/architecture/` (FR-022): the capability README and `VERIFICATION.md`, the repository `README.md`,
  `AGENTS.md` (Current State and capability command bullets), and `src/analysts/README.md`.

The documentation task must make the listed narratives and every status statement the feature affects true for the
merged state, re-reading the documents at implementation time; the rewrites above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-193130-media-quality-usable-intervals/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── media-quality-fact.md
│   └── usable-interval-fact.md
└── tasks.md             # /speckit-tasks, not created here
```

### Source Code (repository root)

```text
src/analysts/capabilities/media-context-classification/
├── analyst-manifest.json            # 1.2.0, capability 1.1.0, output 1.1.0, factPayloadSchemas (3 kinds)
├── Dockerfile                       # MANIFEST_SHA256 and labels updated together with the manifest
├── pyproject.toml, uv.lock          # project version 1.1.0 only
├── README.md, VERIFICATION.md       # facts, settings, limits, registration for 1.1.0
├── schemas/
│   ├── shot-boundary.payload.schema.json    # rewritten into the restricted subset
│   ├── media-quality.payload.schema.json
│   └── usable-interval.payload.schema.json
├── audit/                           # regenerated (project version only)
├── src/media_context_classification/
│   ├── __init__.py                  # CAPABILITY_VERSION 1.1.0, fact kind constants
│   ├── settings.py                  # quality constants and settings provenance
│   ├── quality.py                   # streaming per-frame measures and stretch detection (new)
│   ├── intervals.py                 # usable-interval derivation (new)
│   ├── facts.py                     # media-quality and usable-interval fact builders
│   └── __main__.py                  # single decode pass, sorted fact emission
└── tests/                           # new quality, interval, schema, manifest, equivalence, determinism tests;
                                     # support/media.py gains blur and frozen shots

src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── Support/ShotBoundaryAnalystImage.cs   # versions 1.1.0 and fact kinds
├── ShotBoundaryImageTests.cs             # labels, manifest schemas, audit, new kinds, SC-007
└── ShotBoundaryEndToEndTests.cs          # recording with known defects and cuts, all three kinds

docs/architecture/, README.md, AGENTS.md, src/analysts/README.md (status statements; see Architecture Updates)
```

**Structure Decision**: Extend the existing capability project and its existing Docker-backed evidence; two new
modules separate measurement from derivation so each is unit-testable without media.

## Complexity Tracking

No constitution violations.

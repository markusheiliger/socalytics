# Implementation Plan: Shot-Boundary Media Context Analyst (Baseline)

**Branch**: `20261023-022148-shot-boundary-analyst` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-022148-shot-boundary-analyst/spec.md`

## Summary

Add the independent, locked Python project `src/analysts/capabilities/media-context-classification/` that implements
capability `media-context-classification` 1.0.0 on the existing Analyst SDK. For each segment it reads the staged
segment media, decodes every frame of the primary video stream through the pinned FFmpeg build (downscaled raw frames
and exact frame timestamps from two separate invocations), computes a deterministic HSV content-change measure per
consecutive frame pair with OpenCV and NumPy, and applies declared constants: a fixed hard-cut threshold, an adaptive
rule (absolute floor plus a ratio to the surrounding changes), and a minimum shot length. It writes one `shot-boundary`
fact per boundary. A digest-pinned `linux/amd64` image, dependency audit evidence with a license allow-list check,
Docker-backed image and end-to-end tests in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, a documented
registration procedure, and corrected documentation complete the feature. No platform, SDK, contract, Web UI, Registry,
or seed change is made, and `reference-media-probe` stays untouched. The result is development evidence only.

## Technical Context

**Language/Version**: Python 3.12 (`requires-python >=3.12,<3.13`, as the SDK and the reference capability); C# 14 on
.NET 10 for the Docker-backed evidence tests only

**Primary Dependencies**: `socalytics-analyst-sdk` (path source `../../sdk`), `opencv-python-headless` and `numpy`
(exact versions chosen and hash-locked by `uv lock` at implementation; wheels only, no source builds), pinned FFmpeg
and ffprobe 8.1 LGPL build from `.github/actions/environment-setup/analyst-media-toolchain.json`; dev: `hatchling`,
`pytest`, `jsonschema`

**Storage**: N/A (reads the read-only staged media mount, writes the attempt result through the SDK; scratch only)

**Testing**: `pytest` with clips generated at test time by the pinned `ffmpeg`; xUnit v3 Docker-backed tests
(`ShotBoundaryImageTests`, `ShotBoundaryEndToEndTests`) in the EndToEnd test project

**Target Platform**: `linux/amd64` OCI image, non-root, read-only root file system, no network; CPU only

**Project Type**: Analyst capability container (single Python project under `src/analysts/capabilities/`)

**Performance Goals**: a few-second evidence segment completes in seconds; a one-minute 1080p segment completes in
at most 300 seconds (the manifest's `minExecutionTimeoutSeconds`) at the bound below

**Constraints**: peak memory at most 2 GiB for a one-minute 1080p segment (manifest `resources.memory` `2Gi`);
byte-identical facts regardless of host, CPU count, or thread scheduling (`cv2.setNumThreads(1)`, integer arithmetic for
the measure, no randomness); streaming with constant memory per frame and a bounded context window; no network, no
credentials, no model weights

**Scale/Scope**: one segment per attempt; one project, one image, two Docker-backed test classes, documentation updates

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Result | Evidence |
| --- | --- | --- |
| I. Architecture is the authority | Pass | Implements the `media-context-classification` row, Wave 1 (baseline only), and the packaging rules without changing a design decision; `## Architecture Updates` below lists the status corrections. |
| II. Source-area ownership | Pass | The project lives under `src/analysts/capabilities/`, the Analyst area's first project beside the reference capability; no new child of `src/`. Evidence tests live in the existing `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`. |
| III. API-first control plane | Pass | Uses only existing Registry, member fact, and Manager operations; no endpoint, contract, or DB access; the control plane never handles video bytes. |
| IV. Evidence over claims | Pass | Unit tests on generated clips, image tests, an end-to-end test against exact known truth, and an audit test; all labelled development evidence. |
| V. Focused, minimal changes | Pass | One new project, two test classes plus helpers, documentation corrections; no SDK, platform, Web UI, seed, or environment change. |
| Technology and tooling (environment) | Pass | The environment actions already provide CPython 3.12, `uv`, and the pinned FFmpeg/ffprobe, and `analyst-projects.py` discovers and verifies every project under `src/analysts/capabilities/` with the locked sequence. OpenCV and NumPy arrive as hash-locked wheels from the lock, like the SDK's dependencies. No standalone environment spec is needed; see [research.md](research.md) R-02. |
| Workflow gates | Pass | Documentation passes `node .github/scripts/check-markdown.mjs`; no product CI is added. |

**Post-design re-check**: unchanged; the Phase 1 design adds no platform contract, dependency outside the Analyst
area, or environment requirement.

## Architecture Updates

**Narratives**:

- [Low-Level Capabilities](../../docs/architecture/analyst-capability-catalog.md#low-level-capabilities): the
  `media-context-classification` row (baseline of shot boundaries implemented as development evidence; types,
  live/replay/graphics, quality, usable intervals, the learned challenger, and gates not implemented).
- [Implementation Waves](../../docs/architecture/analyst-capability-catalog.md#implementation-waves): Wave 1 starts with
  the media-context baseline; no Wave 1 gate is met.
- [Catalog Rules](../../docs/architecture/analyst-capability-catalog.md#catalog-rules): the paragraph that says
  development reference capabilities are not part of the catalog.
- [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging):
  the first catalog-named capability image, and the audit evidence it starts.
- [Analyst runtime and recovery](../../docs/architecture/analyst-runtime-and-recovery.md): the statement that real
  Analyst execution evidence is a development reference that is not a catalog capability.

**Design updates**: None applied. The plan decides capability-local details only (detection settings, the fact payload
shape, frame and timestamp acquisition, the audit evidence layout); none of them is a platform design decision, and
they are documented in the capability's README and contract, not in `docs/architecture/`.

**Status statements at merge** (the implementation re-reads the documents; these are examples, not the whole job):

- `analyst-capability-catalog.md`, Catalog Rules: "Development reference capabilities are not part of this catalog. The
  first one, `reference-media-probe` …" stays true for the reference capability; add that the first catalog-named
  capability, `media-context-classification`, exists as a baseline of shot boundaries only (development evidence,
  passing none of the gates, no learned classifier, never in a production workflow).
- `analyst-capability-catalog.md`, Low-Level Capabilities and Wave 1 ("implement media context, person and ball
  detection …"): state that only the shot-boundary baseline of media context is implemented; shot type, live, replay,
  graphics, quality, usable intervals, and the challenger are not.
- `analysts-models-and-hardware.md`, Image Metadata And Packaging: after the reference paragraph, name the second image
  and its additions (OpenCV and NumPy wheels, a dependency audit with an allowed-license set and an SBOM of the
  capability layers, no model weights); keep the statements that registries, publication, the catalog's license and
  provenance audit, and production values remain open.
- `analyst-runtime-and-recovery.md` (around "development evidence, not a catalog capability or a production Analyst
  image"): reconcile with the first catalog-named capability image.
- Any statement elsewhere that no catalog capability exists (`README.md`, `AGENTS.md`, `src/analysts/README.md`, which
  the documentation task also corrects as listed in the tasks).

The documentation task MUST make the listed narratives and every status statement this feature affects true for the
merged state, re-reading the documents at implementation time, with the listed rewrites as examples rather than the
whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-022148-shot-boundary-analyst/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── shot-boundary-fact.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
src/analysts/capabilities/media-context-classification/
├── Dockerfile                      # repository-root build, same convention as the reference image
├── Dockerfile.dockerignore
├── README.md                       # settings, limitations, registration procedure, contract, evidence status
├── .gitignore
├── analyst-manifest.json
├── pyproject.toml
├── uv.lock
├── audit/
│   ├── dependency-audit.json       # name, version, license, source per direct and transitive dependency
│   └── sbom.cdx.json               # CycloneDX SBOM of the capability layers
├── docker/
│   └── fetch_verified.py           # hash-verified download helper (same pinned approach as the reference)
├── schemas/
│   └── shot-boundary.payload.schema.json
├── scripts/
│   └── generate_audit.py           # regenerates audit/ from uv.lock and installed metadata
├── src/media_context_classification/
│   ├── __init__.py                 # CAPABILITY_ID, CAPABILITY_VERSION, FACT_KIND
│   ├── __main__.py                 # SDK entry point
│   ├── settings.py                 # declared detection constants
│   ├── frames.py                   # timestamps (ffprobe) and downscaled raw frames (ffmpeg)
│   ├── measure.py                  # HSV content-change measure
│   ├── detector.py                 # hard and adaptive rules, minimum shot length, streaming window
│   └── facts.py                    # boundary to Fact (range, payload, provenance)
└── tests/                          # pytest: unit, determinism, failures, audit, packaging, payload schema

src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── ShotBoundaryImageTests.cs       # labels = manifest, restricted run, memory and timeout bound, no weights
├── ShotBoundaryEndToEndTests.cs    # registration, run, facts versus known truth
└── Support/
    ├── ShotBoundaryAnalystImage.cs # build and push helper (the reference helper stays unchanged)
    └── CutMedia.cs                 # synthetic recording with known cuts

README.md, AGENTS.md, src/analysts/README.md, docs/architecture/   # documentation corrections
```

**Structure Decision**: one new capability project that mirrors the reference capability's layout and packaging, so the
environment's project discovery, the Dockerfile convention, and the SDK entry point apply unchanged. Tests that need a
container, a registry, and the platform stay in the existing EndToEnd project because that is where the Docker-backed
infrastructure lives; the new helper classes sit beside, and do not modify, the reference helpers.

## Complexity Tracking

No Constitution Check violations; nothing to justify.

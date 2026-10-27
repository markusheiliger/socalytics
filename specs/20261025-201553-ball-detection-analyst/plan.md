# Implementation Plan: Ball Detection Analyst (Development Baseline)

**Branch**: `20261025-201553-ball-detection-analyst` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-201553-ball-detection-analyst/spec.md`

## Summary

Add the independent, locked Python project `src/analysts/capabilities/ball-detection/` that implements capability
`ball-detection` 1.0.0 on the existing Analyst SDK. For each segment it reads the staged segment media, selects frames by
the fixed sampling rule `fixed-5fps-v1` on the frames' own timestamps (pinned FFmpeg and ffprobe), decodes only those
frames at full size, and runs one full-frame pass per frame: letterbox to 640x640, the pinned YOLOX-S ONNX model on ONNX
Runtime CPU (single-threaded), head decoding, selection of the COCO `sports ball` class, a confidence floor, one duplicate
merge, a per-frame maximum, remap to source-frame coordinates, and a single-frame visibility state. It writes one
`ball-observation` fact per retained observation, with the payload schema declared in the manifest `1.2.0` and enforced
by the SDK and by acceptance. A digest-pinned `linux/amd64` image with the model fetched at build from an audited source
at a pinned digest, dependency and model audit evidence with a CycloneDX bill of materials, Docker-backed image and
end-to-end tests in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, a documented registration procedure, and
corrected documentation complete the feature. No platform, SDK, contract, Web UI, Registry, seed, or environment-action
change is made, and the existing capabilities stay untouched. The result is development evidence only; the evaluation
harness requirement is recorded as deferred because the harness is not implemented.

## Technical Context

**Language/Version**: Python 3.12 (`requires-python >=3.12,<3.13`, as the SDK and the other capabilities); C# 14 on
.NET 10 for the Docker-backed evidence tests only

**Primary Dependencies**: `socalytics-analyst-sdk` (path source `../../sdk`), `opencv-python-headless`, `numpy`, and
`onnxruntime` (CPU; the exact versions are the ones the environment spec pins, hash-locked by `uv lock`, wheels only),
pinned FFmpeg and ffprobe 8.1 LGPL build from `.github/actions/environment-setup/analyst-media-toolchain.json`; dev:
`hatchling`, `pytest`, `jsonschema`

**Storage**: N/A (reads the read-only staged media mount and the model file in the image, writes the attempt result through
the SDK; scratch only)

**Testing**: `pytest` with clips generated at test time by the pinned `ffmpeg` and a scripted inference seam; tests that need
the real model skip without the file; xUnit v3 Docker-backed tests (`BallDetectionImageTests`,
`BallDetectionEndToEndTests`) in the EndToEnd test project, which always run the real model

**Target Platform**: `linux/amd64` OCI image, non-root, read-only root file system, no network; CPU only

**Project Type**: Analyst capability container (single Python project under `src/analysts/capabilities/`)

**Performance Goals**: a few-second evidence segment is analyzed in at most 120 seconds; a one-minute 1080p segment
(about 300 analyzed frames) completes within the manifest's `minExecutionTimeoutSeconds` of 300

**Constraints**: peak memory at most 2 GiB (manifest `resources.memory` `2Gi`); byte-identical facts regardless of CPU count
or thread scheduling (single-threaded ONNX Runtime and OpenCV, sequential execution); one decode batch of at most 32 frames
in memory at a time; no network, no credentials, no run-time download

**Declared values**: recall bound 0.5 with centre distance at most the truth diameter ([research.md](research.md) R-12),
sampling rule `fixed-5fps-v1`, confidence floor 0.20, merge IoU 0.45, at most 5 observations per frame, visibility
threshold 0.50 ([research.md](research.md) R-03 and R-06), and single-threaded inference. Cross-host tolerances
([research.md](research.md) R-15): coordinates within 1.0 px and confidence within 0.001 across hosts, relative centre
position within 0.01 across source resolutions.

**Scale/Scope**: one segment per attempt; one project, one image, one model, two Docker-backed test classes, documentation
updates

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Result | Evidence |
| --- | --- | --- |
| I. Architecture is the authority | Pass | Implements the first slice of the `ball-detection` row and the container-owned preprocessing rule without changing a design decision; `## Architecture Updates` lists the status corrections. |
| II. Source-area ownership | Pass | The project lives under `src/analysts/capabilities/`; evidence tests live in the existing `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`; no new child of `src/`. |
| III. API-first control plane | Pass | Uses only existing Registry, member fact, evidence-playback, and Manager operations; no endpoint, contract, or database access; the control plane never handles video bytes. |
| IV. Evidence over claims | Pass | Unit tests on generated clips, image tests, an end-to-end test against known truth with a fixed recall bound, and an audit test; everything is labelled development evidence and no accuracy claim exceeds the fixtures. |
| V. Focused, minimal changes | Pass | One new project, two test classes plus helpers, documentation corrections; no SDK, platform, Web UI, seed, or environment change; no dependency on `person-role-detection`. |
| Technology and tooling (environment) | Pass | The OpenCV, NumPy, and ONNX Runtime CPU wheels and the locked verification sequence come from the dependent environment spec [CV and Inference Verification Environment](../archive/20261023-085421-cv-inference-environment/spec.md), named under the spec's Assumptions → Dependencies and implemented first; this feature never edits the environment actions. See [research.md](research.md) R-09 and R-10. |
| Workflow gates | Pass | Documentation passes `node .github/scripts/check-markdown.mjs`; no product CI is added. |

**Post-design re-check**: unchanged; the Phase 1 design adds no platform contract, dependency outside the Analyst area, or
environment requirement beyond the dependent environment spec. Open implementation stop rules (model audit, recall bound)
are recorded in [research.md](research.md) R-01 and R-12.

## Architecture Updates

**Narratives**:

- [Low-Level Capabilities](../../docs/architecture/analyst-capability-catalog.md#low-level-capabilities): the
  `ball-detection` row (first single-frame slice implemented as development evidence: pretrained generic model, full-frame
  only), and the paragraph below the table that states which capabilities are implemented.
- [Catalog Rules](../../docs/architecture/analyst-capability-catalog.md#catalog-rules): the paragraphs on development
  reference capabilities and on the first catalog-named capability, and the statement that no catalog capability declares
  payload schemas, where they are still true or false after this feature.
- [Implementation Waves](../../docs/architecture/analyst-capability-catalog.md#implementation-waves): Wave 1 status; no
  Wave 1 gate is met.
- [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging): a
  development image with model weights and the audit evidence of the model.
- [Result manifest and lineage](../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage): the
  statement about which capabilities declare payload schemas.
- [Analyst runtime and recovery](../../docs/architecture/analyst-runtime-and-recovery.md) and the Docker Desktop adapter
  evidence statements: the list of capabilities verified end to end.
- [Platform implementation](../../docs/architecture/platform-implementation.md) (the `src/analysts` status bullet that
  names the first catalog-named capability): `ball-detection` exists as a first single-frame slice with a pretrained
  development model.

**Design updates**: None applied. The plan decides capability-local details only (model choice, class selection, sampling
rule, postprocessing constants, visibility rule, payload shape, audit layout, test seams). The architecture already states
that preprocessing is container-owned and that the first detector is a full-frame pass with tiled profiles planned; the
pretrained development model and the two-state visibility rule are capability-local development choices documented in the
capability's README and contract, not platform design decisions.

**Status statements at merge** (the implementation re-reads the documents; these are examples, not the whole job):

- `analyst-capability-catalog.md`, `ball-detection` row and the paragraph below the table ("Of this table, only …
  is implemented"): add the ball-detection first slice and state that the separately fine-tuned RT-DETRv2-S, the YOLOX-S
  challenger, SAHI-style tiled and hybrid inference, CUDA and TensorRT profiles, ball and small-object AP gates, trajectory,
  and calibration are not implemented.
- `analyst-capability-catalog.md`, Catalog Rules and Wave 1: add the capability as a development capability with a
  pretrained model passing no gate, correct the wording about how many development or catalog-named capabilities exist, and
  correct "no catalog capability declares payload schemas" wherever it would become false.
- `analysts-models-and-hardware.md`, Image Metadata And Packaging and result manifest and lineage: add the development
  image with model weights fetched at build by digest and the model audit; "no model weights" must stay true only for the
  images that have none; keep registries, publication, the catalog's licence and provenance audit, and production values
  open.
- `analyst-runtime-and-recovery.md` and the Docker Desktop adapter evidence sentence: the verified capabilities list gains
  `ball-detection`.
- The evaluation harness paragraph ("decided but not implemented yet") stays true; add that the capability's evaluation is
  deferred until it exists.
- Any statement elsewhere about which capabilities exist (`README.md`, `AGENTS.md`, `src/analysts/README.md`, which the
  documentation task also corrects).

The documentation task MUST make the listed narratives and every status statement this feature affects true for the merged
state, re-reading the documents at implementation time, with the listed rewrites as examples rather than the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-201553-ball-detection-analyst/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── ball-observation-fact.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
src/analysts/capabilities/ball-detection/
├── Dockerfile                      # repository-root build, same convention as the other capability images
├── Dockerfile.dockerignore
├── README.md                       # settings, limitations, registration procedure, evidence and deferral status
├── .gitignore
├── analyst-manifest.json           # manifest 1.2.0 with the payload schema
├── pyproject.toml
├── uv.lock
├── audit/
│   ├── dependency-audit.json       # per dependency, native library, FFmpeg, and the model
│   └── sbom.cdx.json               # CycloneDX SBOM of the capability layers
├── docker/
│   └── fetch_verified.py           # hash-verified download helper (toolchain and model)
├── model/
│   └── model-record.json           # source, licence, training-data terms, digest, status
├── schemas/
│   └── ball-observation.payload.schema.json
├── scripts/
│   └── generate_audit.py
├── src/ball_detection/
│   ├── __init__.py                 # CAPABILITY_ID, CAPABILITY_VERSION, FACT_KIND
│   ├── __main__.py                 # SDK entry point
│   ├── settings.py                 # declared constants
│   ├── frames.py                   # timestamps (ffprobe), sampling rule, batched full-size decode (ffmpeg)
│   ├── preprocess.py               # letterbox and remap
│   ├── model.py                    # digest check, ONNX Runtime session, Detector protocol
│   ├── postprocess.py              # head decode, class selection, floor, merge, cap, clip, rounding
│   ├── visibility.py               # single-frame visibility rule
│   └── facts.py                    # observation to Fact (range, payload, provenance)
└── tests/                          # pytest: unit with scripted detector, real-model tests, determinism, failures, audit, payload schema

src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── BallDetectionImageTests.cs
├── BallDetectionEndToEndTests.cs
└── Support/
    ├── BallDetectionAnalystImage.cs   # build and push helper (existing helpers stay unchanged)
    └── BallMedia.cs                   # synthetic recording with a rendered ball and known truth

README.md, AGENTS.md, src/analysts/README.md, docs/architecture/   # documentation corrections
```

**Structure Decision**: one new capability project mirroring the shot-boundary capability's layout and packaging, so the
environment's project discovery, the Dockerfile convention, and the SDK entry point apply unchanged. Docker-backed tests stay
in the existing EndToEnd project where the container, registry, and platform infrastructure live; new helpers sit beside and
do not modify the existing ones. The project shares no code with `person-role-detection`, so the two features can be
implemented in either order.

## Complexity Tracking

No Constitution Check violations; nothing to justify.

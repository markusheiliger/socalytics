# Implementation Plan: Person and Role Detection Analyst (Development Baseline)

**Branch**: `20261025-195345-person-role-detection` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-195345-person-role-detection/spec.md`

## Summary

Add the independent, locked Python project `src/analysts/capabilities/person-role-detection/` that implements capability
`person-role-detection` 1.0.0 on the existing Analyst SDK. For each segment it reads the staged segment media, selects
frames by the fixed sampling rule `fixed-2fps-v1` on the frames' own timestamps (pinned FFmpeg and ffprobe), decodes only
those frames at full size, and runs one full-frame pass per frame: letterbox to 640x640, the pinned YOLOX-S ONNX model on
ONNX Runtime CPU (single-threaded), head decoding, a confidence floor, one duplicate merge, a per-frame maximum, and remap
to source-frame coordinates. COCO `person` maps to the role-unknown `person` (class mapping `1.0.0`); no specific role is
emitted. It writes one `person-role-detection` fact per retained detection, with the payload schema declared in the
manifest `1.2.0` and enforced by the SDK and by acceptance. A digest-pinned `linux/amd64` image with the model fetched at
build from an audited source at a pinned digest, dependency and model audit evidence with a CycloneDX bill of materials,
Docker-backed image and end-to-end tests in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`, a documented
registration procedure, and corrected documentation complete the feature. No platform, SDK, contract, Web UI, Registry,
seed, or environment-action change is made, and the existing capabilities stay untouched. The result is development
evidence only; the evaluation harness requirement is recorded as deferred because the harness is not implemented.

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
the real model skip without the file; xUnit v3 Docker-backed tests (`PersonRoleDetectionImageTests`,
`PersonRoleDetectionEndToEndTests`) in the EndToEnd test project, which always run the real model

**Target Platform**: `linux/amd64` OCI image, non-root, read-only root file system, no network; CPU only

**Project Type**: Analyst capability container (single Python project under `src/analysts/capabilities/`)

**Performance Goals**: a few-second evidence segment is analyzed in at most 120 seconds; a one-minute 1080p segment
(about 120 analyzed frames) completes within the manifest's `minExecutionTimeoutSeconds` of 300

**Constraints**: peak memory at most 2 GiB (manifest `resources.memory` `2Gi`); byte-identical facts regardless of CPU
count or thread scheduling (single-threaded ONNX Runtime and OpenCV, sequential execution); one decoded frame and one
decode batch in memory at a time; no network, no credentials, no run-time download

**Declared values**: recall bound 0.6 at IoU 0.5 ([research.md](research.md) R-11), sampling rule `fixed-2fps-v1`,
confidence floor 0.30, merge IoU 0.45, at most 100 detections per frame ([research.md](research.md) R-02 and R-03), and
single-threaded inference. Cross-host tolerances ([research.md](research.md) R-14): box coordinates within 1.0 px and
confidence within 0.001 across hosts, relative box position within 0.02 of the frame size across source resolutions.

**Scale/Scope**: one segment per attempt; one project, one image, one model, two Docker-backed test classes, documentation
updates

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Result | Evidence |
| --- | --- | --- |
| I. Architecture is the authority | Pass | Implements the first detection slice of the `person-role-detection` row and the container-owned preprocessing rule without changing a design decision; `## Architecture Updates` lists the status corrections. |
| II. Source-area ownership | Pass | The project lives under `src/analysts/capabilities/`; evidence tests live in the existing `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests`; no new child of `src/`. |
| III. API-first control plane | Pass | Uses only existing Registry, member fact, evidence-playback, and Manager operations; no endpoint, contract, or database access; the control plane never handles video bytes. |
| IV. Evidence over claims | Pass | Unit tests on generated clips, image tests, an end-to-end test against known truth with a fixed recall bound, and an audit test; everything is labelled development evidence and no accuracy claim exceeds the fixtures. |
| V. Focused, minimal changes | Pass | One new project, two test classes plus helpers, documentation corrections; no SDK, platform, Web UI, seed, or environment change. |
| Technology and tooling (environment) | Pass | The OpenCV, NumPy, and ONNX Runtime CPU wheels and the locked verification sequence come from the dependent environment spec [CV and Inference Verification Environment](../archive/20261023-085421-cv-inference-environment/spec.md), named under the spec's Assumptions → Dependencies and implemented first; this feature never edits the environment actions. See [research.md](research.md) R-08, R-10. |
| Workflow gates | Pass | Documentation passes `node .github/scripts/check-markdown.mjs`; no product CI is added. |

**Post-design re-check**: unchanged; the Phase 1 design adds no platform contract, dependency outside the Analyst area, or
environment requirement beyond the dependent environment spec. Open implementation stop rules (model audit, recall bound)
are recorded in [research.md](research.md) R-01 and R-11.

## Architecture Updates

**Narratives**:

- [Low-Level Capabilities](../../docs/architecture/analyst-capability-catalog.md#low-level-capabilities): the
  `person-role-detection` row (first detection slice implemented as development evidence: pretrained generic model,
  role-unknown `person` only, full-frame only), and the paragraph below the table that says only the shot-boundary baseline
  is implemented.
- [Catalog Rules](../../docs/architecture/analyst-capability-catalog.md#catalog-rules): the paragraphs on development
  reference capabilities and on the first catalog-named capability, including the sentence that the reference capability
  remains the only other development capability and the statement that no catalog capability declares payload schemas.
- [Implementation Waves](../../docs/architecture/analyst-capability-catalog.md#implementation-waves): Wave 1 has started with
  the shot-boundary baseline only.
- [Image Metadata And Packaging](../../docs/architecture/analysts-models-and-hardware.md#image-metadata-and-packaging): a
  third development image, the first with model weights, and the audit evidence of the model.
- [Result manifest and lineage](../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage): the
  statement that no catalog capability declares payload schemas in its manifest.
- [Source-area list](../../docs/architecture/platform-implementation.md) (the `src/analysts` bullet): status statement that
  `person-role-detection` exists as a first detection slice with a pretrained development model (development evidence only,
  no catalog gate met, never in a production workflow).
- [Analyst runtime and recovery](../../docs/architecture/analyst-runtime-and-recovery.md) and the Docker Desktop adapter
  evidence statements in `analysts-models-and-hardware.md`: the list of capabilities verified end to end.

**Design updates**: None applied. The plan decides capability-local details only (model choice, class mapping, sampling
rule, postprocessing constants, payload shape, audit layout, test seams). The architecture already states the decision that
preprocessing is container-owned and that the first detector is full-frame; the pretrained development model and the
role-unknown `person` value are capability-local development choices documented in the capability's README and contract,
not platform design decisions.

**Status statements at merge** (the implementation re-reads the documents; these are examples, not the whole job):

- `analyst-capability-catalog.md`, Catalog Rules: "The first catalog-named capability, `media-context-classification`,
  exists as a shot-boundary baseline only … and the reference capability remains the only other development capability":
  add that `person-role-detection` exists as a first detection slice with a pretrained development model (role-unknown
  `person` only, full frame, passing no gate), and correct the "only other development capability" wording.
- `analyst-capability-catalog.md`, the payload-schema sentence "but no catalog capability declares them yet": state that
  `person-role-detection` declares its fact payload schema.
- `analyst-capability-catalog.md`, Low-Level Capabilities and Wave 1 ("Of this table, only the shot-boundary baseline …
  is implemented", "Wave 1 has started only with the shot-boundary baseline"): add the person-role-detection first slice
  and state that the fine-tuned RT-DETRv2-S, the YOLOX challenger, tiled and hybrid inference, CUDA and TensorRT profiles,
  per-role AP gates, and calibration are not implemented and no Wave 1 gate is met.
- `analysts-models-and-hardware.md`, Image Metadata And Packaging: after the second development image, name the third and
  its additions (ONNX Runtime CPU, the pretrained model fetched at build by digest, model audit); "no model weights" must stay
  true only for the first two images; keep registries, publication, the catalog's licence and provenance audit, and
  production values open.
- `analysts-models-and-hardware.md`, result manifest and lineage: "no catalog capability declares payload schemas in its
  manifest yet".
- `analyst-runtime-and-recovery.md` and the Docker Desktop adapter evidence sentence: the verified capabilities list gains
  `person-role-detection`.
- The evaluation harness paragraph ("decided but not implemented yet") stays true; the feature adds that the capability's
  evaluation is deferred until it exists.
- Any statement elsewhere that only two development or one catalog-named capability exists (`README.md`, `AGENTS.md`,
  `src/analysts/README.md`, which the documentation task also corrects).

The documentation task MUST make the listed narratives and every status statement this feature affects true for the merged
state, re-reading the documents at implementation time, with the listed rewrites as examples rather than the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-195345-person-role-detection/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── person-role-detection-fact.md
├── checklists/
│   └── requirements.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
src/analysts/capabilities/person-role-detection/
├── Dockerfile                      # repository-root build, same convention as the other capability images
├── Dockerfile.dockerignore
├── README.md                       # settings, limitations, class mapping, registration procedure, evidence and deferral status
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
│   └── person-role-detection.payload.schema.json
├── scripts/
│   └── generate_audit.py
├── src/person_role_detection/
│   ├── __init__.py                 # CAPABILITY_ID, CAPABILITY_VERSION, FACT_KIND
│   ├── __main__.py                 # SDK entry point
│   ├── settings.py                 # declared constants
│   ├── frames.py                   # timestamps (ffprobe), sampling rule, batched full-size decode (ffmpeg)
│   ├── preprocess.py               # letterbox and remap
│   ├── model.py                    # digest check, ONNX Runtime session, Detector protocol
│   ├── postprocess.py              # head decode, class mapping, floor, merge, cap, clip, rounding
│   └── facts.py                    # detection to Fact (range, payload, provenance)
└── tests/                          # pytest: unit with scripted detector, real-model tests, determinism, failures, audit, payload schema

src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── PersonRoleDetectionImageTests.cs
├── PersonRoleDetectionEndToEndTests.cs
└── Support/
    ├── PersonRoleDetectionAnalystImage.cs   # build and push helper (existing helpers stay unchanged)
    └── FigureMedia.cs                       # synthetic recording with rendered figures and known truth

README.md, AGENTS.md, src/analysts/README.md, docs/architecture/   # documentation corrections
```

**Structure Decision**: one new capability project mirroring the shot-boundary capability's layout and packaging, so the
environment's project discovery, the Dockerfile convention, and the SDK entry point apply unchanged. Docker-backed tests stay
in the existing EndToEnd project where the container, registry, and platform infrastructure live; new helpers sit beside and
do not modify the existing ones.

## Complexity Tracking

No Constitution Check violations; nothing to justify.

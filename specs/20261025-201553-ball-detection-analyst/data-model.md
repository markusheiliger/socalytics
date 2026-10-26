# Data Model: Ball Detection Analyst (Development Baseline)

The feature adds no platform table, migration, or contract. The entities below are in-process or file shapes of the
capability project; stored facts use the platform's existing fact envelope ([contract](contracts/ball-observation-fact.md)).

## Detection settings (`settings.py`)

| Name | Value (development) | Meaning |
| --- | --- | --- |
| `DETECTOR_VERSION` | `1.0.0` | equals the capability version; a change of any value is a new version |
| `SAMPLING_RULE` | `fixed-5fps-v1` | one analyzed frame per 200 ms grid point anchored at the window start |
| `INPUT_SIZE` | 640 | letterboxed square model input, pad value 114 |
| `SOURCE_CLASS` | `sports ball` (COCO id 32) | the only class kept |
| `CONFIDENCE_FLOOR` | 0.20 | minimum of objectness times class score |
| `MERGE_IOU` | 0.45 | greedy non-maximum suppression threshold, once per frame |
| `MAX_OBSERVATIONS_PER_FRAME` | 5 | cap after the merge |
| `VISIBILITY_RULE_VERSION` | `1.0.0` | `confidently-visible` at confidence 0.50 or above, else `not-confidently-visible` |
| `DECODE_BATCH` | 32 | analyzed frames decoded per FFmpeg invocation |
| `THREADS` | 1 | ONNX Runtime intra and inter op threads, OpenCV threads |

## Model record (`model/model-record.json`)

| Field | Content |
| --- | --- |
| `id`, `version` | `yolox-s`, upstream release tag |
| `source` | upstream release URL of the ONNX file |
| `licence` | `Apache-2.0` (code and released weights) |
| `trainingDataTerms` | COCO 2017 terms as recorded by the audit |
| `retrievedOn`, `sha256` | filled when the file is first fetched; the single pin for build, start, label, and audit |
| `conversion` | `none` (upstream ONNX) |
| `status` | `development evidence` |

## Entities in process

- **Observation**: box (left, top, width, height in source pixels), centre, confidence, visibility state, frame index,
  frame size. Never linked to another observation.
- **Analyzed frame**: zero-based file index, match time in ms, source width and height, raw BGR pixels (one at a time).
- **Fact**: envelope `kind`, `startMs`, `endMs`, `confidence`, and the payload of the contract.

## Flow

1. Read the job and staged media; verify the model digest (`model-missing`, `model-digest-mismatch`).
2. `ffprobe` frame timestamps; choose analyzed frames by the sampling rule inside the nominal window.
3. Decode the chosen frames at full size in batches of 32; per frame: letterbox, infer, decode head, keep the ball class
   above the floor, merge, cap, remap, clip, round, derive the visibility state. No state is carried between frames.
4. Build facts (range ends at the next analyzed frame or the window end), validate against the payload schema, write the
   result through the SDK.

## Evidence artifacts

| Artifact | Purpose |
| --- | --- |
| `analyst-manifest.json` | declaration, including the payload schema |
| `schemas/ball-observation.payload.schema.json` | the same schema for tests; a test asserts equality with the manifest |
| `audit/dependency-audit.json`, `audit/sbom.cdx.json` | licence and provenance evidence of dependencies, native libraries, FFmpeg, and the model |
| `model/model-record.json` | model source, licence, training-data terms, digest |

# Contract: `ball-observation` fact of `ball-detection` 1.0.0

Development evidence. The contract is component-local; the authoritative copy after implementation is the capability's
README and `schemas/ball-observation.payload.schema.json`, validated by the project's tests. It adds no platform
contract: the facts use the existing fact envelope (`contracts/analysis/fact-artifact`), and the payload is
capability-owned and declared in the manifest (`output.factPayloadSchemas`, manifest `1.2.0`).

## Envelope

| Field | Value |
| --- | --- |
| `kind` | `ball-observation` |
| `startMs` | match time of the analyzed frame; inside the segment's nominal half-open window |
| `endMs` | match time of the next analyzed frame, or the nominal window end for the last analyzed frame; greater than `startMs`, never beyond the window end |
| `confidence` | the payload confidence |
| result output type | `foundational-facts`, schema version `1.0.0` |

One fact per retained observation and no other fact. Facts are ordered by `startMs`, then confidence descending, then box
top, then box left, and are byte-identical for identical staged media on the same image and CPU architecture class. A
segment without an observation is an accepted result with no facts.

## Payload

| Property | Type | Description |
| --- | --- | --- |
| `box` | object | `left`, `top`, `width`, `height` in source-frame pixels, numbers; coordinates `0` to `16384`, sizes greater than 0 |
| `centre` | object | `x`, `y` in source-frame pixels, numbers `0` to `16384`; the centre of the box |
| `frame` | object | `width`, `height` integers of the source frame, at least 1 |
| `confidence` | number 0 to 1 | the model score after the declared postprocessing |
| `visibility` | object | `state` (enum `confidently-visible`, `not-confidently-visible`) and `ruleVersion` (`1.0.0`) |
| `frameIndex` | integer >= 0 | zero-based index of the analyzed frame in the staged segment file, lead-in frames counted |
| `model` | object | `id` (`yolox-s`), `version`, `digest` (`sha-256:` + 64 hex), `sourceClass` (`sports ball`), `sourceClassId` (32) |
| `settings` | object | all eleven properties required: `samplingRule` (string, `fixed-5fps-v1`), `inputSize` (integer, 640), `confidenceFloor` (number, 0.20), `mergeRule` (string, `nms`), `mergeIou` (number, 0.45), `maxObservationsPerFrame` (integer, 5), `profile` (string, `full-frame`), `detectorVersion` (string, `1.0.0`), `padValue` (integer, 114), `interpolation` (string, `linear`), `visibilityThreshold` (number, 0.50); each is a single-value enum of its version's constant |

No other property is allowed at any level (`additionalProperties: false`). The model digest in every fact equals the
manifest's `model.digest`.

## Behavior that is part of the contract

- A single full-frame pass per analyzed frame; no tile, tiled, or hybrid inference; settings are constants of the version
  and are never read from the job, profile, or Manager.
- Boxes are clipped to the frame; boxes empty (below 1 px) after clipping are dropped. The centre is the centre of the
  clipped box.
- The duplicate merge is applied once per frame; no pair of boxes above the merge IoU remains in a frame.
- At most 5 facts per analyzed frame, highest confidence first, ties by position. The capability does not decide which
  candidate is the match ball.
- Frames outside the nominal window are never analyzed. No other frame than the analyzed one contributes to a fact.
- No track or ball identifier, interpolation, smoothing, velocity, pitch coordinate, occlusion, off-frame, or in-play
  claim. A missing observation means "not detected in this frame", never "no ball".
- Containment of the box in the exact frame is guaranteed by the capability and asserted in its tests; the payload schema
  bounds coordinates but cannot compare them with `frame` (JSON Schema limit, see [research.md](../research.md) R-07).

## Failure reasons (attempt ends as failed, no result written)

| Reason | When |
| --- | --- |
| `segment-media-required` | the job has no staged segment media |
| `media-decode-failed` | a tool cannot start or exits non-zero, the media is undecodable, or timestamps and frames differ in count |
| `media-no-video-stream` | the staged file has no video stream |
| `timestamp-unmappable` | a frame in the requested interval cannot be mapped to the segment's window |
| `model-missing` | the model file is absent or unreadable |
| `model-digest-mismatch` | the model file's digest differs from the pin |
| `model-inference-failed` | the inference session cannot be created or run |

Acceptance refuses violating payloads with `result-payload-invalid` (SDK and platform) and leaves no visible fact.

## Manifest declaration (summary)

`manifestVersion` `1.2.0`; `capability.id` `ball-detection`, `version` `1.0.0`, `tier` `low-level`, `executionScope`
`segment`; `upstream` has exactly one entry, `capabilityId` `media-context-classification`, `capabilityVersion` `1.0.0`, `schemaVersion` `1.0.0`, `kind` `optional` (no coverage block), which this version does not consume; `videoAccess` `required`; `output.schemaVersion`
`1.0.0` with `factPayloadSchemas` for `ball-observation`; `compatibleProfiles` `["ball-detection-dev"]`; `model.digest`
the pinned model digest; `runtime.constraints` `["linux-amd64"]`, no accelerators; `resources` memory `2Gi`, cpu `2`;
`requirements.minExecutionTimeoutSeconds` `300`; `determinism` deterministic and idempotent. The manifest is valid against
`contracts/analysis/analyst-manifest/v1`.

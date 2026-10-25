# Contract: `media-quality` fact of `media-context-classification` 1.1.0

Development evidence. The contract is component-local; the authoritative copy during and after implementation is the
capability's README and `schemas/media-quality.payload.schema.json`, validated by the project's tests. It adds no
platform contract: the facts use the existing fact envelope (`contracts/analysis/fact-artifact`), and the payload is
capability-owned. All values are fixed, uncalibrated development values.

## Envelope

| Field | Value |
| --- | --- |
| `kind` | `media-quality` |
| `startMs` | match time of the first frame of the stretch (for `frozen-frame`: the original frame that is then repeated); inside the segment's nominal half-open window |
| `endMs` | match time of the first frame after the stretch, or the nominal window end; greater than `startMs`, never beyond the window end |
| `confidence` | `0.5` |
| result output type | `foundational-facts`, schema version `1.1.0` |

One fact per defect stretch and rule. Facts are in `startMs` order and byte-identical for identical staged media. A
stretch is defined by frame measures only and may span a shot boundary.

## Payload

| Property | Type | Description |
| --- | --- | --- |
| `rule` | `"black-frame"`, `"frozen-frame"`, or `"low-detail"` | the rule that fired |
| `measure` | number, 3 decimals | worst per-frame value of the stretch that still satisfied the rule: mean luma (0 to 255), mean absolute luma difference, or mean absolute Laplacian |
| `threshold` | number | the threshold compared against (`16.0`, `0.5`, or `2.0`); `measure <= threshold` |
| `minDurationMs` | integer | minimum duration applied (`300`, `500`, or `1000`) |
| `frameCount` | integer, at least 1 | in-window frames in the stretch (frozen includes the original frame) |
| `settings` | object | `qualityVersion`, `frameWidth`, `frameHeight`, `ruleOrder`, the three thresholds, and the three minimum durations; no other property |

Undeclared properties are forbidden at every level.

## Behavior that is part of the contract

- Black takes precedence: `frozen-frame` and `low-detail` never fire on frames the black rule fired on; they can fire
  together on other frames.
- The minimum duration is judged on the in-window part of the stretch only; lead-in and following frames only serve
  as predecessors. A stretch shorter than its minimum yields no fact.
- Frame times come from the frames' own timestamps mapped to match time, not from a nominal rate.
- A segment with an empty window yields no facts. A single-frame segment is evaluated by the black and low-detail
  rules only (no frozen-frame stretch, as it has no duplicate) and yields a `media-quality` fact only when its clipped
  range reaches the rule's minimum duration. A segment without a stretch yields no `media-quality` fact.

## Failure reasons

The baseline's reasons: `segment-media-required`, `media-decode-failed`, `media-no-video-stream`,
`timestamp-unmappable`. A failure never yields a partial result. Payload violations are rejected by the SDK and by
result acceptance with the platform's named `result-payload-*` reasons.

## Manifest declaration summary

`output.factPayloadSchemas["media-quality"]` equals `schemas/media-quality.payload.schema.json` (restricted subset,
manifest `1.2.0`, output schema version `1.1.0`).

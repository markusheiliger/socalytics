# Contract: `shot-boundary` fact of `media-context-classification` 1.0.0

Development evidence. The contract is component-local; the authoritative copy during and after implementation is the
capability's README and `schemas/shot-boundary.payload.schema.json`, validated by the project's tests. It adds no
platform contract: the facts use the existing fact envelope (`contracts/analysis/fact-artifact`), and the payload is
capability-owned.

## Envelope

| Field | Value |
| --- | --- |
| `kind` | `shot-boundary` |
| `startMs` | match time of the first frame of the new shot; inside the segment's nominal half-open window |
| `endMs` | match time of the next in-window frame, or the nominal window end for the last frame; always greater than `startMs` and never beyond the window end |
| `confidence` | `0.5` (rule-based, uncalibrated) |
| result output type | `foundational-facts`, schema version `1.0.0` |

One fact per detected boundary and no fact for any other frame pair. Facts are in `startMs` order and byte-identical for
identical staged media. A segment without a boundary is an accepted result with no facts.

## Payload

| Property | Type | Description |
| --- | --- | --- |
| `rule` | `"hard-cut"` or `"adaptive-cut"` | the rule that detected the boundary |
| `changeMeasure` | number, 0 to 255, 3 decimals | content-change measure of the frame pair |
| `hardThreshold` | number | `30.0`; compared against for every fact |
| `adaptiveFloor` | number | `8.0`; present on adaptive cuts |
| `adaptiveRatio` | number | `3.0`; present on adaptive cuts |
| `contextMean` | number | adaptive cuts only: mean of the surrounding measures the ratio applied to |
| `frameIndex` | integer >= 1 | zero-based index of the first frame of the new shot in the decoded stream of the staged file; at least 1 because a boundary needs a predecessor |
| `previousShotLastFrameMs` | integer or null | match time of the last frame of the previous shot; null when that frame is a lead-in frame outside the staged timeline |
| `settings` | object | provenance: `detectorVersion`, `frameWidth`, `frameHeight`, `adaptiveWindow`, `minShotLengthMs`, `ruleOrder` |

No other property is allowed. Hard-cut facts carry `hardThreshold` and `changeMeasure`; adaptive-cut facts additionally
carry `adaptiveFloor`, `adaptiveRatio`, and `contextMean`.

## Detection behavior that is part of the contract

- Hard cut first, adaptive cut second; one pair yields at most one fact.
- A boundary less than 500 ms after the previous reported boundary is not reported and leaves no trace.
- A boundary is reported only when the first frame of the new shot is in the nominal window; a cut between the last
  lead-in frame and the first in-window frame is reported, a cut among lead-in frames is not.
- A cut between two segments (last frame of one, first of the next) is detected by neither; this is a documented
  limitation of the baseline.
- Gradual transitions (fades, dissolves) and flashes are not detected as such; an adaptive cut may fire at one frame pair.

## Failure reasons (attempt ends as failed, no result written)

| Reason | When |
| --- | --- |
| `segment-media-required` | the job has no staged segment media |
| `media-decode-failed` | a tool cannot start or exits non-zero, the media is undecodable, or timestamps and frames differ in count |
| `media-no-video-stream` | the staged file has no video stream |
| `timestamp-unmappable` | a frame in the requested interval cannot be mapped to the segment's window |

## Manifest declaration (summary)

`capability.id` `media-context-classification`, `version` `1.0.0`, `tier` `low-level`, `executionScope` `segment`,
`upstream` empty, `videoAccess` `required`, `output.schemaVersion` `1.0.0`, `compatibleProfiles`
`["media-context-classification-dev"]`, model digest `sha-256:` + 64 zeros, `runtime.constraints` `["linux-amd64"]`,
no accelerators, `resources` memory `2Gi` and cpu `2`, `determinism` deterministic and idempotent. The manifest is
valid against `contracts/analysis/analyst-manifest/v1`; the payload schema is not declared in it (see
[research.md](../research.md) R-06).

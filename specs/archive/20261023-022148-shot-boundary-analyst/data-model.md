# Data Model: Shot-Boundary Media Context Analyst (Baseline)

The feature adds no platform table, migration, or contract. The entities below are in-process or file shapes of the
capability project; stored facts use the platform's existing fact envelope.

## Detection settings (`settings.py`)

| Name | Value (development) | Meaning |
| --- | --- | --- |
| `DETECTOR_VERSION` | `1.0.0` | equals the capability version; a change of any value below is a new version |
| `FRAME_WIDTH`, `FRAME_HEIGHT` | 64, 36 | downscaled decode size (area interpolation) |
| `HARD_THRESHOLD` | 30.0 | pair measure at or above is a hard cut |
| `ADAPTIVE_FLOOR` | 8.0 | lower absolute bound of the adaptive rule |
| `ADAPTIVE_RATIO` | 3.0 | multiple of the surrounding mean required by the adaptive rule |
| `ADAPTIVE_WINDOW` | 4 | frame pairs on each side of the pair, inside the nominal window only |
| `MIN_SHOT_LENGTH_MS` | 500 | minimum distance between the starts of two reported boundaries |
| `RULE_ORDER` | `hard-cut`, `adaptive-cut` | a pair is tested in this order and yields at most one boundary |
| `CONFIDENCE` | 0.5 | fixed fact confidence, "rule-based, uncalibrated" |

## Decoded frame

| Field | Type | Rule |
| --- | --- | --- |
| `index` | int >= 0 | zero-based position in the decoded video stream of the staged file (lead-in frames included) |
| `media_ms` | int | `floor(pts * time_base * 1000)` from `best_effort_timestamp` |
| `match_ms` | int or none | `SegmentMedia.to_match_ms(media_ms)`; none outside the staged timeline spans |
| `pixels` | `uint8[36][64][3]` BGR | downscaled raw frame; never stored beyond the current pair and the context window |

A frame is *in window* when it lies in the requested source interval and `window.start_ms <= match_ms < window.end_ms`.

## Pair measure

| Field | Type | Rule |
| --- | --- | --- |
| `measure` | decimal, 3 places | HSV mean absolute difference as defined in [research.md](research.md) R-03; 0 to 255 |
| `second_frame` | decoded frame | must be in window; the first frame has the previous decoded frame as predecessor, in window or not |

## Shot boundary (detector output)

| Field | Type | Rule |
| --- | --- | --- |
| `frame_index` | int | `index` of the first frame of the new shot |
| `start_ms` | int | `match_ms` of that frame, inside the nominal window |
| `rule` | `hard-cut` or `adaptive-cut` | first rule in `RULE_ORDER` that fires |
| `measure` | decimal | the pair's measure |
| `context_mean` | decimal or none | adaptive rule only: mean of the surrounding measures |
| `previous_frame_ms` | int or none | `match_ms` of the predecessor frame, none when it is a lead-in frame outside the timeline |

Constraints: boundaries are strictly ordered by `start_ms`; two boundaries are at least `MIN_SHOT_LENGTH_MS` apart; one
pair yields at most one boundary; a boundary needs its predecessor and an in-window second frame.

## Shot-boundary fact

The platform's existing fact envelope with `kind` `shot-boundary`, `start_ms` = boundary `start_ms`, `end_ms` = the
match time of the next in-window frame, or the nominal window end for the last frame, raised to `start_ms + 1` when the
next frame floors to the same millisecond and never beyond the window end, `confidence` 0.5, and the payload of
[contracts/shot-boundary-fact.md](contracts/shot-boundary-fact.md). Facts are written in `start_ms` order, which is
the order the boundaries are found. The capability writes a result with zero facts for a segment without a boundary.

## Dependency audit record (`audit/dependency-audit.json`)

| Field | Type | Rule |
| --- | --- | --- |
| `name`, `version` | string | normalized name and exact version equal to `uv.lock` |
| `license` | SPDX expression | must be in the allowed set (permissive, or LGPL for a separately packaged shared library) |
| `scope` | `runtime`, `dev`, `bundled-library`, `toolchain` | `runtime` and `dev` come from the lock; the others are the OpenCV-bundled FFmpeg libraries and the pinned FFmpeg build |
| `source` | string | package index file or release URL identity |

`audit/sbom.cdx.json` is the CycloneDX 1.5 document of the same components plus the base image digest.

## State transitions

A segment attempt is one pass: `start` -> decode timestamps -> stream frames -> detect -> write result (accepted with
zero or more facts) or fail with a named reason and write nothing. There is no state between attempts.

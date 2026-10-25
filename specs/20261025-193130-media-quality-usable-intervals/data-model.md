# Data Model: Media Quality and Usable Intervals

Python types are internal to `src/analysts/capabilities/media-context-classification`; the platform sees only facts
(`contracts/analysis/fact-artifact`) with capability-owned payloads.

## Quality settings (`settings.py`)

| Constant | Value |
| --- | --- |
| `QUALITY_VERSION` | `1.0.0` |
| `FRAME_WIDTH` x `FRAME_HEIGHT` | 64 x 36 (shared with the cut detector) |
| `BLACK_LUMA_MAX` | 16.0 |
| `FROZEN_DIFFERENCE_MAX` | 0.5 |
| `LOW_DETAIL_MAX` | 2.0 |
| `BLACK_MIN_MS` / `FROZEN_MIN_MS` / `LOW_DETAIL_MIN_MS` | 300 / 500 / 1000 |
| `QUALITY_RULE_ORDER` | `black-frame`, `frozen-frame`, `low-detail` |
| `CONFIDENCE` | 0.5 (existing) |

A change to any value is a new capability version.

## FrameObservation (internal, per in-window frame)

`index` (decode index), `match_ms`, `luma_mean` (exact fraction), `detail` (exact fraction), `difference`
(exact fraction or none without a non-black in-window or lead-in predecessor).

## Stretch (internal) and `media-quality` fact

| Field | Rule |
| --- | --- |
| `rule` | `black-frame`, `frozen-frame`, or `low-detail` |
| `start_ms` | match time of the first frame (for frozen: the original, repeated frame) |
| `end_ms` | match time of the first frame after the stretch, or the window end; `> start_ms`, `<= window end` |
| `measure` | maximum per-frame measure in the stretch, 3 decimals; always `<= threshold` |
| `frame_count` | in-window frames of the stretch (frozen includes the original) |

Validation: `end_ms - start_ms >= minDurationMs` of the rule, otherwise discarded. One stretch per maximal run of
frames; at most one fact per rule and run; a stretch is never reported by two segments (in-window frames only).

A frozen run opens at the preceding in-window frame when a frame pair qualifies, and a frozen stretch requires `frame_count` of at least 2 (at least one duplicate) in addition to the minimum duration.

State transitions per rule (streaming): `idle -> running` on the first qualifying in-window frame (for frozen: the qualifying pair's original frame), `running ->
running` on a qualifying frame, `running -> closed` on a non-qualifying in-window frame (end = that frame's match
time) or on the end of the window (end = window end); closed stretches below the minimum are dropped. Black
qualification is evaluated first; frozen and low-detail never qualify on a black frame.

## UsableInterval (internal) and `usable-interval` fact

| Field | Rule |
| --- | --- |
| `start_ms`, `end_ms` | consecutive ranges; first `start_ms` = window start, last `end_ms` = window end |
| `usable` | true if and only if no stretch covers the range, except that the span before the first in-window frame takes that frame's verdict |
| `reasons` | rule ids of the covering stretches in `QUALITY_RULE_ORDER`; empty when usable |

Invariants: no gap, no overlap, adjacent ranges differ in `usable` or `reasons`, every shot boundary whose two
sides differ in verdict or reasons coincides with an edge, a boundary alone never makes a range unusable.

## Shot-boundary fact

Unchanged (`DetectedBoundary`, `shot_boundary_fact`, `DETECTOR_VERSION` `1.0.0`); only its declared manifest schema is
rewritten into the restricted subset (research R2).

## Fact order

`(startMs, kind order, rule order, endMs)`, kind order `shot-boundary`, `media-quality`, `usable-interval`.

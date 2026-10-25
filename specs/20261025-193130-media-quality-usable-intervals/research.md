# Research: Media Quality and Usable Intervals

All unknowns of the Technical Context are resolved here. Every value is a fixed development value, not calibrated.

## R1: Versions

- **Decision**: Capability version `1.1.0` (additive fact kinds), analyst manifest version `1.2.0` (the version that
  carries `output.factPayloadSchemas`), output schema version `1.1.0` (the fact set of the output changed). The
  Python distribution version becomes `1.1.0` (`pyproject.toml`, and the project's own entry in `uv.lock` through
  `uv lock --offline`). `DETECTOR_VERSION` stays `1.0.0` because the cut detector is untouched, so `shot-boundary`
  facts stay byte-identical to the baseline (a stronger result than FR-002 requires). A new `QUALITY_VERSION` `1.0.0`
  versions the quality settings.
- **Rationale**: A changed output set needs a new output schema version so profile and run pins differ from `1.0.0`;
  the previous `1.0.0` image stays registrable by its own digest.
- **Alternatives**: Keep output `1.0.0` (rejected: pins of old and new profiles would be indistinguishable); bump
  `DETECTOR_VERSION` (rejected: would change shot-boundary bytes for no meaning change).

## R2: Manifest payload schemas must be a restricted rewrite

- **Finding**: The accepted `schemas/shot-boundary.payload.schema.json` uses `$schema`, `if/then`, `dependentRequired`,
  and `"type": ["integer","null"]`. The restricted subset (archived spec `20261023-010826-fact-payload-schemas`, R2)
  allows only `type` (one name), `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`,
  `minimum`/`maximum`/`exclusive*`, `minLength`/`maxLength`, `minItems`/`maxItems`, `$defs`, `$ref`, `title`,
  `description`; limits 16 KiB, depth 8, 256 nodes per schema.
- **Decision**: The files in `schemas/` are exactly the schemas the manifest declares, written in the subset, and a
  test asserts byte-level JSON equality between each file and `output.factPayloadSchemas`. For `shot-boundary`:
  remove `$schema`; make `adaptiveFloor`, `adaptiveRatio`, and `contextMean` optional properties (the conditional
  requirement moves to a project test that checks them against `rule`); declare `previousShotLastFrameMs` without
  `type` and with `minimum: 0` (a keyword that only constrains numbers, so `null` and integers pass and strings are
  not excluded, documented as a known looseness of the subset). Facts keep emitting `null` unchanged.
- **Rationale**: Emitting facts unchanged (FR-002) while declaring a schema the platform and SDK accept (FR-009).
  The SDK validates every fact automatically, so an under-specified schema is the only cost.
- **Alternatives**: Omit the key when null (rejected: changes fact bytes); keep the strict schema as an extra
  project-only file (kept as a test helper in `tests/`, not a second authoritative copy).

## R3: Frame measures (all on the 64 x 36 BGR frame the baseline already decodes)

All sums are integers, converted to exact fractions and quantized to three decimals with the baseline's
`quantize` (ROUND_HALF_EVEN), so results do not depend on host or thread count (`cv2.setNumThreads(1)` as baseline).

| Rule id | Per-frame measure | Fires when | Min duration |
| --- | --- | --- | --- |
| `black-frame` | mean luma, `cv2.COLOR_BGR2GRAY`, 0 to 255 | mean luma `<= 16.0` | 300 ms |
| `frozen-frame` | mean absolute luma difference to the predecessor frame | difference `<= 0.5` | 500 ms |
| `low-detail` | mean absolute Laplacian of the luma (`cv2.Laplacian`, `CV_16S`, `ksize` 1), integer sum / pixels | detail `<= 2.0` | 1000 ms |

- **Reported measure of a stretch**: the worst value that still satisfied the rule (maximum over the stretch's
  per-frame measures), so `measure <= threshold` always holds and the fact shows how close the stretch was.
- **Precedence** (clarification): a frame the black rule fired on is excluded from the frozen and low-detail rules;
  a frozen pair needs both frames non-black. Frozen and low-detail can fire together on other frames.
- **Frozen start**: the stretch starts at the original frame that is repeated; `frameCount` includes it. A frozen
  run is a maximal run of in-window frames where each frame after the first has pair difference `<= threshold` to its
  predecessor; the first in-window frame with an out-of-window or missing predecessor starts the run itself (the
  predecessor is used only for the pair difference, never counted). The run opens at the preceding in-window frame
  when a frame pair qualifies, and a frozen stretch needs `frameCount >= 2` (at least one duplicate) in addition to the
  minimum duration, so a lone first frame never forms a frozen stretch.
- **Stretches** are defined by frame measures only, never split at shot boundaries (clarification).
- **Alternatives**: SSIM or Laplacian variance (rejected: float-order sensitivity, extra cost, no integer exactness);
  HSV measures (rejected for brightness: luma is the standard black-frame criterion).
- **Library check**: only `opencv-python-headless` and `numpy`, already locked. No new dependency; the audit
  (`generate_audit.py --check`) must stay green after the version bump, which is the only change to `uv.lock`.

## R4: Stretch timing, window clipping, and minimum duration

- A stretch covers in-window frames only. Its range is `[first frame match ms, match ms of the first frame after the
  stretch)`; when the stretch reaches the last in-window frame the end is the nominal window end (same convention as
  `fact_end_ms`). The range is clipped to the half-open window and is non-empty.
- Minimum duration is judged on that clipped range (`end - start >= minDurationMs`), per clarification, so each
  segment decides alone and lead-in or following frames only serve as predecessors.
- Frame timestamps come from `media.to_match_ms`, with the baseline's `timestamp-unmappable` failure; no nominal frame
  rate is used (variable frame rate safe).
- **Single-frame segment**: black and low-detail are evaluated; frozen needs a predecessor, so a lone first frame has
  none. Its range ends at the window end. **Empty segment**: no in-window frame, no facts at all (not even a
  usable-interval).

## R5: Usable-interval derivation

- Inputs: the final stretch list (after minimum-duration filtering) and the shot boundaries. A sweep over the sorted
  distinct edge points (stretch starts and ends, clamped to the window) produces sub-ranges, each with the set of
  rules covering it; `usable = (set is empty)`; `reasons` in the fixed order `black-frame`, `frozen-frame`,
  `low-detail` (reason code vocabulary equals the rule ids, a closed enum); adjacent sub-ranges with equal verdict and
  reasons are merged.
- Shot boundaries: a boundary is an edge only where the verdict or reasons change, which by construction happens
  only at stretch edges. A hard cut from normal to black therefore yields the edge at the boundary's match time
  because the black stretch starts at that frame. A cut inside a stretch or inside normal footage is not an edge. The
  derivation function still receives the boundaries and a test asserts the property for every boundary (an edge
  exists at each boundary whose neighbours differ); the function never lets a boundary alone change the verdict.
- The first interval starts at the nominal window start and the last ends at the window end (partition without gap or
  overlap, SC-002); when the first in-window frame is later than the window start, the span before it takes that
  frame's verdict. Only intervals are extended this way; `media-quality` facts keep the first frame's time.

## R6: Facts, ordering, and the single pass

- Frames are decoded once. A pass-through generator feeds each frame to the quality analyzer and then to the
  baseline `detect` (pull style, unchanged). Memory stays bounded: the analyzer keeps the previous frame, running
  stretch states, and the finished stretches (few) and boundaries until the end of the segment.
- All facts are built after decoding, then sorted by `(startMs, kind order, rule order, endMs)` with the kind order
  `shot-boundary`, `media-quality`, `usable-interval`, so output is byte-identical regardless of scheduling. Any
  failure raises before anything is added to the result (no partial result).
- Confidence stays `0.5` for every fact and the result confidence `0.5` when any fact exists, as the baseline.

## R7: Payload shapes

- `media-quality`: `rule`, `measure`, `threshold`, `minDurationMs`, `frameCount`, `settings`.
- `usable-interval`: `usable` (boolean), `reasons` (array of the rule-id enum, at most 3, empty when usable),
  `settings`.
- `settings` of both (closed object): `qualityVersion`, `frameWidth`, `frameHeight`, `ruleOrder`, and the three
  thresholds and three minimum durations by name (flat integer and number properties), so a fact carries every
  value it depended on (FR-005).
- Each schema forbids undeclared properties and stays well below 16 KiB, depth 8, 256 nodes.

## R7b: Resources and runtime bounds

- The added work is a few NumPy operations per 64 x 36 frame; memory and CPU stay inside the baseline manifest
  bounds (`2Gi`, `2`), so the manifest keeps them and `minExecutionTimeoutSeconds` 300. SC-007 (30 s for the
  evidence segment, one-minute 1080p within bounds) is checked in the existing Docker-backed image test, extended
  with the new kinds.

## R8: Verification, registration, and evidence changes

- **Python tests** (project): synthetic clips extend `tests/support/media.py` (`Shot` gains `blur_sigma` and a
  `frozen` repeat flag; black is a flat dark colour, near-black a flat colour just under the threshold); cover every
  case of FR-016.
- **Docker-backed**: `ShotBoundaryAnalystImage` constants move to `1.1.0` and `1.1.0` output; `ShotBoundaryImageTests`
  checks labels and the manifest including `factPayloadSchemas`; `ShotBoundaryEndToEndTests` gets a recording with
  known defects and cuts spanning at least two segments and compares all three kinds with the known truth. The
  baseline equivalence of `shot-boundary` facts is proved by project tests against the unchanged detector and
  golden clips (SC-004), not by running the old image.
- **Dockerfile**: manifest hash `ARG` and labels (`io.socalytics.analyst-manifest.version` `1.2.0`,
  `io.socalytics.capability.version` `1.1.0`, output schema label if present) are updated together with the manifest.
- **Registration**: the README procedure changes to version `1.1.0`, `outputSchemaVersion` `1.1.0`, and notes that
  `1.0.0` stays registrable under its own digest.
- **Environment**: the tasks need only the already-locked libraries and the pinned toolchain the existing
  analyst-media environment provides; the dependency on twin 256 in the spec stands.

# Contract: `usable-interval` fact of `media-context-classification` 1.1.0

Development evidence. The contract is component-local; the authoritative copy during and after implementation is the
capability's README and `schemas/usable-interval.payload.schema.json`, validated by the project's tests. It adds no
platform contract. All values are fixed, uncalibrated development values.

## Envelope

| Field | Value |
| --- | --- |
| `kind` | `usable-interval` |
| `startMs` / `endMs` | one range of the segment's nominal half-open window; the facts of a segment together partition the window exactly: the first starts at the window start, the last ends at the window end, with no gap and no overlap |
| `confidence` | `0.5` |
| result output type | `foundational-facts`, schema version `1.1.0` |

Facts are in `startMs` order and byte-identical for identical staged media. A segment with no in-window frame has no
facts of any kind.

## Payload

| Property | Type | Description |
| --- | --- | --- |
| `usable` | boolean | `true` if and only if no quality rule applies to the range |
| `reasons` | array of `"black-frame"`, `"frozen-frame"`, `"low-detail"`, at most 3 | the rules that apply, in that fixed order; empty when `usable` is `true`, non-empty otherwise |
| `settings` | object | as for `media-quality`; no other property |

Undeclared properties are forbidden at every level.

## Derivation (part of the contract)

- Derived only from the final `media-quality` stretches and the shot boundaries of the segment.
- Edges are the stretch starts and ends within the window; adjacent ranges with identical `usable` and `reasons` are
  merged.
- A shot boundary alone never makes a range unusable. A boundary at which the verdict or reasons change coincides with
  an interval edge; a boundary inside a range that does not change them is not an edge.
- Cuts across segment boundaries remain a documented baseline limitation.

## Failure reasons

As for `media-quality`.

## Manifest declaration summary

`output.factPayloadSchemas["usable-interval"]` equals `schemas/usable-interval.payload.schema.json`. The consistency
of `usable` and `reasons` (empty if and only if usable) is not expressible in the restricted subset and is checked by
the project's tests.

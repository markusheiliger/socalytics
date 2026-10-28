# Data Model: Deterministic Evidence Part State Test

The feature adds, changes, and removes no persisted entity, column, migration, or contract. It observes the existing Segment Service and evidence records.

## Observed entities

| Entity | Source | Fields the test reads | Notes |
| ------ | ------ | --------------------- | ----- |
| Materialized segment | `socalytics.materialized_segments` | `state`, `demanded`, `segment_number`, lease columns | After the first resolution: one demanded row; after `MaterializeSegmentsAsync`: `available` |
| Evidence resolution part | Response of `POST /api/v1/analysis/runs/{runId}/evidence-media` | `state`, `retryAfterSeconds`, `media.url` | `materializing` with a positive hint and no media, then `available` with a link |

## State transitions under test

```text
absent (rows deleted) --evidence resolution--> materializing (demanded) --explicit MaterializeSegmentsAsync--> available
```

## Validation rules

- Exactly one demanded segment after the first resolution; no other actor changes the rows between the steps.
- The explicit `MaterializeSegmentsAsync` call is the only materialization in the scenario unless a test enables the worker explicitly.

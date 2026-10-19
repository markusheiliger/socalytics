# Research: Partial-Coverage Match Barriers

## R1 — Where the declaration lives

- **Decision**: An optional `coverage` object on each `upstream` entry of the Analyst manifest
  (`analyst-manifest` 1.1.0): `{ "mode": "partial" }` with an optional `minimumAcceptedFraction` (`0 < f <= 1`).
  `mode` is the closed enum `["partial"]`; absence means complete coverage.
- **Rationale**: The architecture's [Capability Declaration](../../../docs/architecture/analysts-models-and-hardware.md#capability-declaration)
  owns the required upstream capabilities, so the permission to use partial upstream input belongs to the same entry.
  Per dependency lets a capability tolerate gaps in one input but not another. An object instead of a boolean leaves
  room for later rules without another shape change.
- **Alternatives considered**: A capability-wide flag (cannot express mixed dependencies); a workflow-definition edge
  property (the Analyst author, not the workflow author, knows whether the algorithm copes with gaps); a minimum
  segment count (depends on match length and the segmentation policy, so it is not portable between workflows).

## R2 — Minimum coverage rule and rounding

- **Decision**: `requiredAccepted = max(1, ceil(minimumAcceptedFraction × expected))`, with `minimumAcceptedFraction`
  absent meaning the `1` alone. Evaluated over the upstream node's expected segment jobs of the run.
- **Rationale**: Satisfies FR-002 (at least one accepted unit) and gives a deterministic integer test that does not
  depend on floating-point equality at the boundary; `ceil` never lets a result fall below the declared fraction.
- **Alternatives considered**: `round` (could accept 0.49 for 0.5); comparing fractions directly (boundary errors).

## R3 — Validation at import and in the seed

- **Decision**: `AnalystManifestValidator` validates the schema (1.0.0 or 1.1.0) and then the semantic rules: a
  `coverage` entry is refused as `manifest-invalid` with the validation error `partial-coverage` at `/upstream/<i>/coverage` when the capability's `executionScope` is
  `segment` or the entry's `kind` is `optional`; the fraction range is enforced by the schema
  (`exclusiveMinimum: 0`, `maximum: 1`) and reported with the same code. `ConfiguredWorkflowSeeder` builds the seed
  manifest with the edge's configured coverage and runs it through the same validator, so the seed fails visibly
  instead of seeding an invalid declaration.
- **Rationale**: One validation path for imported and seeded declarations (FR-003, FR-005); reusing the existing
  `manifest-invalid` refusal with a named validation error keeps the Registry's refusal codes and OpenAPI problem
  types unchanged. `DeclaredUpstream` (`src/platform/SocAlytics.Platform.Domain/Registry/CapabilityDeclaration.cs`)
  gains the parsed coverage policy.
- **Alternatives considered**: Validating in run creation only (too late; an invalid image would already be
  registered).

## R4 — Carrying the declaration into runs

- **Decision**: `ResolvedWorkflowEdge` gains `CoverageMode` and `MinimumAcceptedFraction`, set by both resolvers from
  the dependent node's declaration entry whose `capabilityId` matches the upstream node's capability. Run creation
  writes them to the new columns of `analysis_dependency_edges`; `WorkflowSnapshotMapper` adds a `coverage` object to
  partial edges only. `RunEdgeState` (Domain) gains a `CoveragePolicy`.
- **Rationale**: The run snapshot is the immutable source for readiness and recovery (FR-004, FR-010); emitting the
  field only for partial edges keeps snapshot digests of existing workflows unchanged.
- **Alternatives considered**: Reading the Registry during readiness (violates run immutability); a JSON column for
  edge options (the checks are simpler and indexed reads unnecessary, but two typed columns keep invariants in the
  database).

## R5 — Readiness rule

- **Decision**: In `ReadinessEvaluator`, an edge is a partial barrier when its policy is partial, its upstream node is
  segment-scoped, and its dependent node is match-scoped. For partial barriers:
  `BlockingUpstream` ignores the upstream node's `Failed`/`Blocked` state; `RequiredSatisfied` requires every relevant
  upstream job final (`Completed`, `Failed`, `Blocked`) and the accepted count at least `requiredAccepted`; a new
  check blocks the dependent node and its jobs, with blocker the upstream node and failure reason
  `upstream-coverage-below-minimum`, when every relevant job is final and the count is below the minimum.
  `Satisfiable` treats a partial barrier as satisfiable while any relevant job is not final or the minimum is met. A
  partial policy on a match-scoped upstream, or on a segment-scoped dependent, is evaluated as complete coverage.
- **Rationale**: Implements FR-007 and FR-008 without touching complete-coverage edges (FR-006); keeping the rule in
  the Domain evaluator keeps restart reconstruction and reconciliation identical (FR-010).
- **Alternatives considered**: Ready as soon as the minimum is met (would snapshot a still-changing coverage and make
  results depend on timing); per-segment blocking for segment chains (clarified out of scope).

## R6 — Input snapshot shape

- **Decision**: `InputSnapshotEntry` gains `CoverageMode` (`complete` or `partial`) and `Coverage`
  (list of `{ segmentId, state }`, ordered by segment window, only for partial barriers). Status is `accepted` when
  every unit was accepted and `partial` otherwise; `AcceptedResultIds` stay the accepted results. Serialized into the
  existing `analysis_logical_job_inputs.inputs` JSON; old snapshots without the fields read as `complete`.
- **Rationale**: FR-009 and immutability; no schema migration for snapshot JSON.
- **Alternatives considered**: A separate coverage table (more joins for every read; the snapshot is written once).

## R7 — Upstream read and staging

- **Decision**: For partial barriers, `ListUpstreamResults` builds coverage from the snapshot's `Coverage` entries
  instead of `CoverageBuilder`'s current-state mapping; other entries keep today's mapping. `upstream-results` 1.1.0
  adds an optional `upstream.coverage` object (`mode`, optional `minimumAcceptedFraction`) emitted for every page; the
  page's `contractVersion` becomes `1.1.0`. `UpstreamStager` keeps writing `coverage.json` and adds the mode to it; the
  staged-inputs layout (Analysis Result Read for Jobs, `contracts/staged-inputs.md` in its archived spec) gains the
  `mode` field additively.
- **Rationale**: FR-012 and FR-013; the read must equal the snapshot even if a blocked or failed state changed after
  readiness (it cannot today, but the read must not depend on it).
- **Alternatives considered**: Adding coverage to the job message (`analyst-job`): duplicates the read and enlarges
  every message.

## R8 — Coverage report and acceptance

- **Decision**: `result-manifest` 1.1.0 adds optional `inputCoverage`: an array (max 64) of
  `{ capability, schemaVersion, units: [{ segmentId | match, state }] }` (max 1024 units, state enum as in
  `upstream-results`). `ResultManifestVerifier` supports `1.0.0` and `1.1.0`; after the lineage check it compares
  reports with the job's coverage: for partial barriers with the snapshot coverage (as the upstream read reports it)
  and rejects with `result-coverage-mismatch` when a partial barrier has no report, a report names an unknown
  dependency, or any unit or state differs; for dependencies without a declaration a report is optional and only its
  `accepted` units are checked (exactly the units of the snapshot's accepted results), since other units' states can
  change after readiness. Order of units is not significant; duplicates are rejected.
- **Rationale**: FR-014 to FR-016; comparing with what the Analyst could read makes the check fair and deterministic.
- **Alternatives considered**: Requiring reports from every 1.1.0 manifest (forces real Analysts that never use
  partial coverage to change); trusting the lineage alone (lineage cannot express failed or blocked segments).

## R9 — Run status read

- **Decision**: `AnalysisNodeStatusView` and `AnalysisNodeStatusDto` gain nullable `InputCoverage`: one item per
  incoming edge with `upstreamNodeKey`, `coverageMode`, `minimumAcceptedFraction`, and counts `accepted`, `failed`,
  `blocked`, `skipped`, `missing`, summed over the node's jobs that have an input snapshot; `null` when no job of the
  node has one. Counts come from snapshot coverage for partial barriers and from the snapshot status otherwise
  (`accepted` entries count their expected units as accepted, `not-applicable` as skipped, `absent` as missing,
  `failed` as failed).
- **Rationale**: FR-017 without new endpoints; additive for the cancellation and supersession features that also extend
  the run status DTO.
- **Alternatives considered**: Extending the fact-indexing reads (FR-018 keeps their semantics) or a new endpoint
  (unnecessary).

## R10 — Simulation and development workflow

- **Decision**: `SimulatedCapabilityOptions.FailSegmentStartsMs` (list of segment window starts in milliseconds) fails
  only those segment jobs with the configured reason; result modes `coverage-mismatch` (flips one unit state) and
  `coverage-omitted` (drops the report); `SimulatedResultWriter` writes `inputCoverage` from the staged
  `coverage.json` files. The development workflow adds the requested node `dev-partial-match-node`
  (`dev-partial-match-capability`, `high-level`, `match`) with a required partial edge from `dev-segment-node` and no
  fraction. The end-to-end test configures a recording of at least two segments under the development segmentation
  policy (or a test segmentation duration short enough) and fails the segment starting at `0`.
- **Rationale**: FR-019 and FR-020; the existing complete-coverage node shows the contrast in the same run.
- **Alternatives considered**: Turning the existing edge partial (loses the complete-coverage demonstration and
  changes existing failure tests).

## R11 — Telemetry

- **Decision**: Counters `socalytics.analysis.partial_barriers_ready` (tag `coverage`: `complete`/`partial`) and
  `socalytics.analysis.partial_barriers_blocked`; coverage rejections use the existing `completions_rejected` counter
  with reason `result-coverage-mismatch`; structured logs carry run and job identifiers already used by analysis logs.
  No audit event.
- **Rationale**: FR-023 with existing dimensions.

## R12 — Coordination with features in implementation

- **Decision**: The fact-indexing feature keeps `result-manifest` 1.0.0 unchanged (its research R1), so this feature
  owns 1.1.0; if it merges first with a version bump, this feature takes the next minor. Cancellation and supersession
  add fields to the same run status DTO; all additions are additive and merge independently. The migration number is
  assigned at implementation time.

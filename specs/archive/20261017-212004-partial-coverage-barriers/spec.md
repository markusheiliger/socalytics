# Feature Specification: Partial-Coverage Match Barriers

**Feature Branch**: `20261017-212004-partial-coverage-barriers`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Partial-Coverage Match Barriers: the Durable Analysis Workflow treats every segment barrier as complete coverage and explicitly deferred \"capability-permitted partial-coverage barriers\"; the architecture defines them (docs/architecture/job-processing.md#readiness-and-dependencies and docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access for coverage states). Today a single permanently failed segment job of a required upstream capability blocks every match-scoped dependent, so a whole match analysis ends `PartiallyCompleted` or `Failed` because of one bad segment. This feature lets a match-scoped capability declare in its Analyst manifest that it permits partial upstream coverage (an additive change to `contracts/analysis/analyst-manifest`, and optionally a minimum coverage rule the plan defines), imported by the Analyst Registry and copied into run and job snapshots; readiness then makes such a match-scoped job ready once every expected upstream segment job is terminal (accepted, failed, blocked, or skipped) and the declared minimum is met, records the per-segment coverage in the job's immutable input snapshot, exposes it in the Manager's upstream read and staged inputs (Analysis Result Read for Jobs), and requires the result manifest to report the coverage it used; acceptance rejects results whose reported coverage disagrees with the snapshot. Capabilities that do not declare it keep complete-coverage behavior unchanged; required/optional/conditional edge semantics stay as implemented; run outcome rules (`Completed`/`PartiallyCompleted`/`Failed`) are applied consistently and the run status read shows coverage. The simulated Analyst and the development seed workflow gain a partial-coverage case so tests and end-to-end evidence exercise it, including failed segments. Out of scope: re-running failed segments, choosing coverage thresholds per run, new capabilities, production values, Web UI changes beyond what existing reads show. Depends on Durable Analysis Workflow, Analyst Registry, Analysis Result Manifests and Validated Acceptance, Analysis Result Read for Jobs, Analyst Manager Work Execution (merged; archived under specs/archive/); coordinate with `specs/archive/20261017-175648-fact-indexing-queries` coverage semantics. Implements docs/architecture/job-processing.md#readiness-and-dependencies (partial coverage), docs/architecture/analysts-models-and-hardware.md (manifest declaration and coverage reporting), and docs/architecture/contracts-and-compatibility.md for the additive contract change."

## Clarifications

### Session 2026-10-17

- Q: Should a failed segment job of a segment-scoped capability block only the same segment's jobs of segment-scoped dependents in this feature? → A: No; segment-to-segment dependencies keep the implemented node-level blocking, and partial coverage applies only to the dependency of a match-scoped capability on a segment-scoped upstream.
- Q: Should the new partial-coverage match-scoped node of the development seed workflow be one of the workflow's requested capabilities? → A: Yes; it is requested, so a fully successful development run ends `Completed` only when that node completes too.
- Q: What minimum coverage does the development partial-coverage node declare? → A: No fraction; at least one accepted segment.
- Q: Should partial-coverage readiness, below-minimum blocking, and coverage rejections be observable in telemetry? → A: Yes; through the existing analysis metrics and structured logs, by outcome and named reason, without new audit events and without segment content or identifiers beyond the run's existing telemetry dimensions.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A match analysis survives one failed segment (Priority: P1)

A Coach uploads a match and its analysis runs. One segment of the match is unusable, so the segment-level
capability permanently fails for that segment while every other segment is accepted. A match-level capability whose
Analyst declares that it permits partial upstream coverage still runs once every expected segment has reached a final
state and the declared minimum coverage is met. It receives exactly the accepted segment results, learns which
segments failed or are blocked, and produces a match-level result that states the coverage it was built from. The run
ends `PartiallyCompleted`, never `Completed`, because one segment failed.

**Why this priority**: This is the user value of the feature: one bad segment no longer discards the match-level
analysis of a whole match. Without it, every other part of the feature has nothing to report.

**Independent Test**: Run the development workflow with the simulated Analyst configured to fail exactly one segment of
the segment-level capability; the partial-coverage match-level node becomes ready after every segment job is final,
its result is accepted with coverage naming the failed segment, and the run ends `PartiallyCompleted`.

**Acceptance Scenarios**:

1. **Given** a match-scoped capability that declares partial coverage for a required segment-scoped upstream and a run
   where one upstream segment job failed permanently and the others were accepted, **When** every expected upstream
   segment job is final and the declared minimum is met, **Then** the match-scoped job becomes ready and its input
   snapshot records each expected segment as accepted or failed.
2. **Given** the same run, **When** some upstream segment jobs are still waiting, ready, claimed, or retrying, **Then**
   the match-scoped job stays waiting, even if the minimum is already met by accepted segments.
3. **Given** every expected upstream segment job is final but fewer segments were accepted than the declared minimum,
   **When** readiness is evaluated, **Then** the match-scoped job becomes blocked by that upstream, exactly as a
   complete-coverage barrier blocks today, and is never published.
4. **Given** a partial-coverage match-scoped result was accepted while an upstream segment failed, **When** the run
   reaches a final state, **Then** the run is `PartiallyCompleted` and not `Completed`.
5. **Given** a partial-coverage capability and a run where every expected upstream segment was accepted, **When** the
   match-scoped job runs and its result is accepted, **Then** its input snapshot and result report complete coverage
   and the run can end `Completed` as today.

---

### User Story 2 - The match-level Analyst sees and reports the coverage it used (Priority: P1)

The Analyst Manager reads the upstream results of a partial-coverage match-level attempt on the Analyst's behalf. The
read returns exactly the accepted results of the job's input snapshot and the per-segment coverage recorded in that
snapshot, and the Manager stages both into the attempt's read-only input mount. The Analyst writes its result
manifest with the coverage it used; the platform accepts the result only when that reported coverage equals the
coverage recorded in the job's input snapshot, and rejects it with a named reason otherwise.

**Why this priority**: "Partial input is never mistaken for a complete match barrier" (job processing architecture)
requires that the coverage is fixed, visible to the Analyst, and verified on acceptance. Readiness without it would
produce match-level results whose completeness nobody can check.

**Independent Test**: For a partial-coverage job with one failed segment, the Manager's upstream read and staged
coverage name the failed segment; a simulated result that reports the snapshot's coverage is accepted, and one that
reports different coverage, or none, ends the attempt as failed with a coverage-mismatch reason.

**Acceptance Scenarios**:

1. **Given** a ready partial-coverage job, **When** its Manager reads the job's upstream results, **Then** the read
   returns only the accepted results recorded in the input snapshot and coverage per expected segment equal to the
   snapshot's, regardless of later upstream state changes.
2. **Given** the read results, **When** the Manager stages them, **Then** the staged inputs include the coverage per
   upstream dependency, and a retried attempt of the same job stages the same coverage.
3. **Given** a partial-coverage job, **When** its result manifest reports coverage equal to the snapshot, **Then** the
   result passes the coverage check, and the other acceptance checks (lineage, scope, provenance, digests) apply
   unchanged.
4. **Given** a partial-coverage job, **When** its result manifest omits the coverage or reports coverage that differs
   from the snapshot in any segment or state, **Then** acceptance rejects the result with a named coverage reason and
   the normal retry rules apply.
5. **Given** a job without any partial-coverage dependency, **When** its result manifest omits coverage, **Then**
   acceptance behaves exactly as today.

---

### User Story 3 - Capabilities declare partial coverage through the Registry (Priority: P2)

A platform operator imports a digest-pinned Analyst image whose manifest declares, for an upstream dependency, that
the capability permits partial coverage and the minimum coverage it needs. The Analyst Registry validates and stores
the declaration, workflow resolution carries it, and every run created from that workflow copies it into its immutable
run and job snapshots, so later Registry changes never alter a running or finished run.

**Why this priority**: The declaration is how a capability opts in; without it only the development seed could
exercise the behavior. It builds on the existing Registry import and needs Story 1 to have any effect.

**Independent Test**: Import a manifest of the new minor version with a partial-coverage declaration and one of the
previous version without it; both are accepted, the declaration is visible in the stored capability declaration, and
a run created afterwards carries it in its snapshot; a manifest with an invalid minimum is refused with a named reason.

**Acceptance Scenarios**:

1. **Given** an Analyst manifest of the new contract version whose match-scoped capability declares partial coverage
   for an upstream dependency with a valid minimum, **When** the image is imported, **Then** the Registry stores the
   declaration with the capability declaration.
2. **Given** an Analyst manifest of the previous contract version, **When** it is imported, **Then** it is accepted as
   today and its upstream dependencies require complete coverage.
3. **Given** a manifest that declares partial coverage on a segment-scoped capability, on an optional dependency, or
   with a minimum outside the allowed range, **When** it is imported, **Then** the Registry refuses it with a named
   validation reason.
4. **Given** a run created from a workflow whose node uses a partial-coverage declaration, **When** the Registry later
   changes, **Then** the run and its jobs keep the declaration and minimum copied at run creation.

---

### User Story 4 - Members see what coverage a match-level result was built from (Priority: P3)

A Coach or Club Admin opens the status of an analysis run. For every node that consumed upstream results, the run
status read states, per upstream dependency, whether partial coverage was permitted and how many expected execution
units were accepted, failed, blocked, skipped, or missing in the input the node's job used, so a
`PartiallyCompleted` run explains which match-level results were built from incomplete input.

**Why this priority**: It makes the outcome understandable to people, but the analysis value (Stories 1 and 2) exists
without it.

**Independent Test**: Read the status of a `PartiallyCompleted` development run with one failed segment as a Coach of
the team; the partial-coverage node shows its input coverage counts with one failed segment, and the complete-coverage
node shows that it is blocked.

**Acceptance Scenarios**:

1. **Given** a run whose partial-coverage node completed from incomplete input, **When** an authorized member reads
   the run status, **Then** the node states that partial coverage was permitted and shows its input coverage counts
   per upstream dependency.
2. **Given** a node whose job has no input snapshot yet, **When** the member reads the run status, **Then** the node
   shows no input coverage rather than inferred values.
3. **Given** a member without read access to the run's team, **When** they read the run status, **Then** the read is
   refused as today.

### Edge Cases

- **All segments accepted**: A partial-coverage job records complete coverage, reports it in its result, and behaves
  like a complete-coverage barrier; the run can end `Completed`.
- **Upstream segment jobs still retrying**: The match-scoped job waits until every expected upstream segment job is
  final; a minimum met early never makes it ready before then.
- **Minimum not met**: The match-scoped job is blocked by the upstream that failed to provide enough coverage, is never
  published, and its descendants follow the normal blocking rules.
- **No accepted segment at all**: Always below the minimum, because the minimum requires at least one accepted
  segment; the job is blocked.
- **Upstream node blocked by its own failed upstream**: Its waiting and ready segment jobs become blocked, those count
  as `blocked` coverage, and segments accepted before the blocking count as accepted; the snapshot records whatever is
  final when the barrier is evaluated.
- **Several upstream dependencies**: Each dependency is evaluated by its own declaration; a dependency without a
  partial-coverage declaration still requires complete coverage, and the job is ready only when every required
  dependency is satisfied.
- **Match-scoped upstream**: A partial-coverage declaration on a dependency whose upstream node is match-scoped has a
  single expected unit; it behaves exactly as complete coverage.
- **Optional and conditional dependencies**: Optional dependencies keep their semantics and never delay readiness. A
  conditional dependency whose predicate applies follows its partial-coverage declaration like a required one; when
  the predicate does not apply, it is `skipped` as today.
- **Retry of the match-scoped attempt**: The input snapshot is immutable; a retry reads, stages, and must report the
  same coverage.
- **Platform restart or reconciliation**: Readiness, snapshot, and blocking decisions are reconstructed from durable
  run state and give the same result.
- **Cancelled or superseded run**: Cancellation and supersession stop readiness as specified by their features; a
  partial-coverage job of a cancelled run is cancelled like any other unfinished job.
- **Runs created before this feature**: Their snapshots have no partial-coverage declaration and keep complete-coverage
  behavior.
- **Analyst ignores the staged coverage**: Its result omits or misreports coverage and is rejected; the normal retry
  rules apply.

## Requirements *(mandatory)*

### Functional Requirements

#### Declaration and Registry

- **FR-001**: The Analyst manifest contract MUST gain an additive minor version that lets a match-scoped capability
  declare, per required or conditional upstream dependency, that it permits partial coverage, together with a minimum
  coverage rule; manifests of the previous version MUST remain valid and mean complete coverage.
- **FR-002**: The minimum coverage rule MUST require at least one accepted expected unit and MAY additionally require a
  minimum accepted fraction of the expected units, a number greater than 0 and at most 1; when no fraction is
  declared, at least one accepted unit is the whole rule.
- **FR-003**: The Analyst Registry MUST validate the declaration on import and refuse, with a named reason, a
  partial-coverage declaration on a segment-scoped capability, on an optional dependency, or with a fraction outside
  the allowed range.
- **FR-004**: The Registry MUST store the declaration with the capability declaration, and run creation MUST copy the
  resolved declaration and minimum into the immutable run and job snapshots, so later Registry changes never alter
  existing runs.
- **FR-005**: The development seed MUST be able to declare partial coverage for a configured edge, with the same
  validation as an imported manifest.

#### Readiness and snapshots

- **FR-006**: A dependency without a partial-coverage declaration MUST keep the implemented complete-coverage
  behavior, and the implemented required, optional, and conditional edge semantics MUST otherwise stay unchanged;
  in particular, dependencies between segment-scoped capabilities keep node-level blocking.
- **FR-007**: For a dependency with a partial-coverage declaration whose upstream is segment-scoped, an upstream node
  that is failed or blocked MUST NOT block the match-scoped dependent by itself; instead the dependency is satisfied
  once every expected upstream segment job is final (accepted, failed, or blocked) and the accepted segments meet the
  declared minimum.
- **FR-008**: When every expected upstream segment job of such a dependency is final and the minimum is not met, the
  match-scoped job MUST become blocked with that upstream as its blocker, and the existing descendant-blocking rules
  MUST apply.
- **FR-009**: When a partial-coverage job becomes ready, its immutable input snapshot MUST record, for each
  partial-coverage dependency, every expected upstream segment with its state (`accepted`, `failed`, or `blocked`),
  the accepted result identities, and that the dependency permitted partial coverage; dependencies without a
  declaration MUST be recorded as today.
- **FR-010**: Readiness decisions for partial-coverage dependencies MUST be serialized per run, reconstructible from
  durable run state after a restart, and re-applied by the analysis reconciliation with the same result.
- **FR-011**: A run in which a partial-coverage node completed while any expected upstream unit was not accepted MUST
  never end `Completed`; the implemented outcome rules MUST decide between `PartiallyCompleted` and `Failed`, and a
  run in which every node completed MUST end `Completed` as today.

#### Upstream read, staging, and result reporting

- **FR-012**: The Manager's upstream read of a partial-coverage job MUST return exactly the accepted results recorded
  in the input snapshot and coverage per expected segment equal to the snapshot, independent of later upstream state
  changes, and MUST state that the dependency permitted partial coverage; the read for other jobs MUST behave as
  today.
- **FR-013**: The Manager MUST stage the coverage of every upstream dependency into the attempt's read-only input
  mount together with the accepted results, as the staged-inputs layout defines.
- **FR-014**: The result manifest contract MUST gain an additive minor version with an optional coverage report per
  upstream dependency (capability, and each expected unit with its state); manifests of the previous version MUST
  remain valid.
- **FR-015**: Acceptance MUST require a coverage report for every partial-coverage dependency of the job and MUST reject
  the result with a named coverage reason when a report is missing or differs from the input snapshot in any unit or
  state; a coverage report for a dependency without a declaration MAY be present and is then checked only against the
  units the input snapshot records as accepted (exactly those units, and no others, are reported `accepted`), because
  the states of units outside the snapshot can change after readiness.
- **FR-016**: Coverage reports MUST NOT relax any existing acceptance check; lineage MUST still name exactly the
  accepted results of the input snapshot.

#### Run status read

- **FR-017**: The member run status read MUST show, for every node whose job has an input snapshot, per upstream
  dependency whether partial coverage was permitted and the number of expected units per coverage state (`accepted`,
  `failed`, `blocked`, `skipped`, `missing`) recorded in the snapshot; nodes without a snapshot show no input
  coverage. Authorization of the read MUST stay unchanged.
- **FR-018**: The fact reads of `specs/archive/20261017-175648-fact-indexing-queries` MUST keep their coverage semantics: the
  coverage of a node's own execution units is unchanged by this feature, and a match-scoped node completed from
  partial input reports its single match window as accepted while the run status read explains its input coverage.

#### Simulation, development workflow, and evidence

- **FR-019**: The simulated Analyst (Development and Test only) MUST be configurable to fail the segment-level
  capability for selected segments only, and MUST report the staged coverage in its result manifest, with a mode that
  deliberately misreports coverage.
- **FR-020**: The development seed workflow MUST include a match-scoped node whose capability declares partial
  coverage of the segment-level node with the minimum of at least one accepted segment and no fraction, alongside
  the existing complete-coverage match-scoped node, so one run shows both behaviors; the new node's capability MUST be
  one of the workflow's requested capabilities.
- **FR-021**: Automated tests MUST cover the declaration's validation and snapshot copying, readiness with waiting,
  satisfied, below-minimum, blocked-upstream, and all-accepted cases, input snapshot immutability, the upstream read
  and staging of coverage, acceptance and rejection of coverage reports, run outcomes, the run status read, and an
  end-to-end run of the development workflow with one failed segment that ends `PartiallyCompleted` with an accepted
  partial-coverage match result.
- **FR-022**: Contract changes MUST follow the repository's contract rules: the previous released schemas stay as
  immutable copies, new examples cover the declaration and coverage reports, and the OpenAPI snapshot is refreshed for
  the run status read.
- **FR-023**: The platform MUST make partial-coverage readiness, below-minimum blocking, and coverage rejections
  observable through the existing analysis metrics and structured logs, by outcome and named reason, without new audit
  events and without telemetry dimensions beyond those the run's analysis telemetry already uses.

### Key Entities

- **Partial-Coverage Declaration**: Part of a capability declaration: for one upstream dependency of a match-scoped
  capability, that partial coverage is permitted and the minimum coverage rule (at least one accepted unit, optional
  minimum accepted fraction).
- **Coverage Entry**: One expected upstream execution unit (a segment, or the match) of one dependency with its state
  `accepted`, `failed`, `blocked`, `skipped`, or `missing`.
- **Input Snapshot**: The immutable record of a logical job's upstream inputs at readiness; for partial-coverage
  dependencies it now includes the coverage entries and the declaration it was evaluated with.
- **Coverage Report**: The part of a result manifest in which the Analyst states, per upstream dependency, the coverage
  it used; acceptance compares it with the input snapshot.
- **Node Input Coverage**: The run status read's per-node, per-dependency summary of the input snapshot's coverage
  counts and whether partial coverage was permitted.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end development run with exactly one failed segment, the partial-coverage match-level
  result is accepted and the run ends `PartiallyCompleted` in 100% of runs, while the complete-coverage match-level
  node is blocked.
- **SC-002**: In automated tests, 100% of partial-coverage match-level jobs become ready only after every expected
  upstream segment job is final, and none becomes ready below its declared minimum.
- **SC-003**: 100% of results whose reported coverage is missing or differs from the input snapshot are rejected with
  a named coverage reason, and 0 such results are accepted.
- **SC-004**: Existing tests of complete-coverage workflows pass unchanged, and manifests of the previous contract
  versions are accepted in 100% of import and acceptance tests.
- **SC-005**: For every partial-coverage node in automated tests, the coverage in the input snapshot, the upstream read,
  the staged inputs, the accepted result, and the run status read agree for 100% of expected units.

## Assumptions

- The minimum coverage rule is fixed per capability declaration; choosing thresholds per run is out of scope.
- Partial coverage applies only to the direct dependency edge of a match-scoped capability on a segment-scoped
  upstream; segment-to-segment dependencies keep node-level blocking as implemented, so a failed segment of an
  intermediate segment-level capability still blocks that capability's other segments (a possible follow-up).
- Re-running failed segments, new real capabilities, production values, and Web UI changes beyond what the existing
  run reads show are out of scope.
- Failed segment jobs stay failed; the existing attempt budget and retry rules decide when a segment job is final.
- The end-to-end evidence uses a recording and segmentation duration that produce at least two segments, so failing
  one segment still leaves an accepted one.
- Depends on the merged Durable Analysis Workflow, Analyst Registry, Analysis Result Manifests and Validated
  Acceptance, Analysis Result Read for Jobs, and Analyst Manager Work Execution (archived under `specs/archive/`), and
  coordinates with `specs/archive/20261017-175648-fact-indexing-queries`, `specs/20261017-182600-analysis-run-cancellation`,
  and `specs/20261017-183953-analysis-run-supersession`, which are being implemented.
- Implements [job processing: readiness and dependencies](../../../docs/architecture/job-processing.md#readiness-and-dependencies),
  the capability declaration and coverage of
  [Analysts, models, and hardware](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access),
  and [contracts and compatibility](../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility)
  for the additive contract versions.

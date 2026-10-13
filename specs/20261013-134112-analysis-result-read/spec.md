# Feature Specification: Analysis Result Read for Jobs

**Feature Branch**: `20261013-134112-analysis-result-read`

**Created**: 2026-10-13

**Status**: Draft

**Input**: User description: "Analysis Result Read for Jobs: a high-level (match-scope) job receives the accepted upstream results it depends on through one capability-scoped, schema-versioned read operation; Analysts never query the database. A Manager-facing, proof-bound operation lets the Manager that holds a current, unfenced attempt query by capability and schema version, optional segment or match-time range, and page, restricted to the attempt's team, match, run, and the job's declared upstream dependencies, returning only accepted results in a stable envelope with each result's validated manifest and short-lived read grants for its artifacts, bounded by the attempt's lease, and reporting coverage explicitly (for every expected upstream segment whether a result is accepted, missing, failed, blocked, or skipped, as the run's state says); reads for fenced, completed, or foreign attempts, for capabilities that are not the job's upstream, or for other runs are refused and audited. Before launching a high-level attempt, the Manager reads the job's upstream results through the operation and places the manifests and artifacts into the attempt's input mount after verifying each artifact's digest, so the Analyst stays free of credentials and network access to the platform. The simulated Analyst of the development workflow's match-level node reads its segment-level results from the input mount and writes lineage from them; the cross-solution end-to-end test proves read, coverage, and lineage. A versioned read envelope contract in contracts/analysis/ is used by both solutions. Out of scope: presigned source-video access for Analysts, fact indexing and queries by people or agents, the Python Analyst SDK and real capabilities, cross-run reads, and production grant lifetimes. Depends on Analysis Result Manifests and Validated Acceptance, Analyst Manager Work Execution, and Durable Analysis Workflow. Implements docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access and the high-level part of the docs/architecture/job-processing.md sequence; because the Manager reads instead of the Analyst, the plan updates that sequence and the Capability-Scoped Result Access section in the same change."

## Clarifications

### Session 2026-10-13

- Q: How much upstream data may the Manager stage into one attempt's input mount? → A: Up to a configured limit (development value 2 GiB per attempt); exceeding it, or lacking that much free space on the host, fails the attempt before launch (category `resource-exhausted`, precise reason in the Manager's local diagnostics; refined at analysis to fit the completion contract's failure categories).
- Q: When does the Manager delete an attempt's staged upstream data? → A: As soon as the attempt ends (completed, failed, fenced, or timed out); leftovers from a crash are removed at Manager start.
- Q: What happens if upstream results change between staging and launch? → A: The Manager stages once per attempt (a retry stages again). The read returns exactly the accepted results and coverage recorded in the job's input snapshot when the job became ready, so later-accepted optional results are not included and the staged inputs equal what acceptance checks as lineage.

## User Scenarios & Testing *(mandatory)*

The actors are machines: the Analyst Manager that runs a high-level attempt, the platform that serves accepted results, and the Analyst that consumes them. The stakeholders are the people who rely on match-level analysis being built only from accepted, authorized evidence.

### User Story 1 - Read a job's accepted upstream results (Priority: P1)

A Manager has claimed a match-level attempt whose job depends on segment-level capabilities. It asks the platform for the accepted results of one upstream capability and schema version, optionally narrowed to some segments or a match-time range, page by page. The platform returns each accepted result's validated manifest and short-lived read grants for its artifacts, and states for every expected upstream segment whether a result is accepted, missing, failed, blocked, or skipped. Nothing outside the attempt's team, match, run, and declared upstream dependencies is visible.

**Why this priority**: Without this read, match-level analysis cannot use what segment-level analysis produced. On its own, it already lets any Manager retrieve exactly the evidence a job is entitled to.

**Independent Test**: For a run with accepted segment-level results, a Manager holding the match-level attempt reads one upstream capability and receives every accepted result with readable artifacts and a coverage entry for every expected segment; a read for another run, a non-upstream capability, or a fenced attempt is refused.

**Acceptance Scenarios**:

1. **Given** a current match-level attempt whose job declares an upstream capability, **When** its Manager reads that capability and schema version, **Then** the platform returns the accepted results recorded in the job's input snapshot for that capability, each with its validated manifest and one short-lived read grant per artifact, and a coverage entry for every expected upstream segment.
2. **Given** more accepted results than one page holds, **When** the Manager follows the continuation, **Then** every accepted result is returned exactly once across pages, in a stable order.
3. **Given** a segment or match-time range, **When** the Manager reads with it, **Then** only results and coverage entries within that range are returned.
4. **Given** an upstream segment whose job failed, is blocked, was skipped, or has no accepted result yet, **When** the Manager reads, **Then** its coverage entry says failed, blocked, skipped, or missing, and no result is returned for it.
5. **Given** an attempt that is fenced, completed, or owned by another Manager, a capability that is not among the job's declared upstream dependencies, or a schema version other than the one the job's dependency pins, **When** the Manager reads, **Then** the read is refused and nothing is disclosed.
6. **Given** read grants, **When** they are used after the attempt's lease or the configured grant lifetime has passed, **Then** storage refuses them.
7. **Given** a revoked Manager, **When** it reads, **Then** its authentication is refused as for any other platform call; an unregistering (draining) Manager still reads for its current attempts.

---

### User Story 2 - Stage upstream results for the Analyst (Priority: P2)

Before the Manager launches a match-level attempt, it reads every upstream dependency of the job, downloads each accepted result's manifest and artifacts with the read grants, verifies each object's digest against the manifest, and places them, together with the coverage, into the attempt's input mount. The Analyst then works only from its input mount and needs no credentials or network access to the platform. If staging cannot complete, the Manager does not launch the Analyst and reports the attempt as failed with a reason.

**Why this priority**: This is how the read reaches the Analyst without giving it any authority. It needs the read (Story 1).

**Independent Test**: Launch a match-level attempt and inspect its input mount: every accepted upstream manifest and artifact is present with verified digests and a coverage file; tamper with a stored artifact and observe that the attempt fails before the Analyst starts.

**Acceptance Scenarios**:

1. **Given** a match-level attempt, **When** the Manager prepares it, **Then** the input mount contains, per upstream dependency, every accepted result's manifest and artifacts and the dependency's coverage, before the Analyst starts.
2. **Given** an artifact whose stored bytes do not match the digest in its manifest, **When** the Manager stages it, **Then** the Manager does not launch the Analyst and reports the attempt as failed with category `input-unavailable`, recording the digest mismatch in its local diagnostics.
3. **Given** read grants that expire during a long download, **When** the Manager continues, **Then** it reads again for fresh grants while the attempt is current and completes staging.
4. **Given** a read that is refused because the attempt was fenced, **When** the Manager stages, **Then** it stops the attempt and sends nothing further, as it does for any fenced attempt.
5. **Given** a segment-level attempt, **When** the Manager prepares it, **Then** no upstream read is made unless the job declares upstream dependencies.

---

### User Story 3 - Match-level analysis from staged results, end to end (Priority: P3)

In the development workflow, the simulated Analyst of the match-level node reads the staged segment-level results from its input mount and writes its result manifest with lineage derived from them. The platform accepts the result only when that lineage matches the job's accepted inputs, as it already does. The cross-solution end-to-end test proves the whole chain: segment results accepted, read, staged, consumed, and the match-level result accepted.

**Why this priority**: It demonstrates the complete high-level path and keeps the development workflow honest about using real upstream evidence. It needs Stories 1 and 2.

**Independent Test**: Run the development workflow end to end and verify that the match-level result's lineage names exactly the segment-level results that were staged, and that a staged segment missing from the input mount leads to a lineage rejection.

**Acceptance Scenarios**:

1. **Given** accepted segment-level results, **When** the match-level attempt runs with the simulated Analyst, **Then** its manifest's lineage is built from the staged results and the platform accepts it.
2. **Given** partial coverage permitted by the capability, **When** some segments are failed or missing, **Then** the match-level result is still produced from the accepted segments and records the coverage it was built from.
3. **Given** the simulated Analyst configured to ignore the staged inputs, **When** the attempt completes, **Then** the platform rejects the result for lineage mismatch.

### Edge Cases

- **No accepted upstream results yet**: A match-level job is only ready once its required upstream barrier is met, so a read normally finds them; if a required upstream result is missing, coverage says so and the Analyst decides only within its declared partial-coverage rules.
- **Optional and conditional dependencies**: Their results are readable when they were accepted by the time the job became ready; their absence is reported as missing or skipped coverage and never blocks the read. An optional result accepted later is not part of this job's inputs.
- **Retried attempt**: A new attempt of the same job stages again from scratch and receives the same inputs, because the job's input snapshot does not change.
- **Very many segments**: Results and coverage are paged; a Manager stages all pages before launch, within the configured per-attempt staging limit.
- **Staging limit or disk space exceeded**: The Manager detects it from the declared artifact sizes before downloading, does not launch the Analyst, and fails the attempt with category `resource-exhausted` (the precise reason stays in its local diagnostics); the platform's retry rules decide whether another Manager tries.
- **Results of an earlier run of the same match**: Never visible; reads are bound to the attempt's run.
- **Attempt fenced during staging**: Further reads are refused; already issued read grants expire; the attempt is abandoned as for any fenced attempt.
- **Storage unavailable**: The read answers with a temporary failure; the Manager retries while the attempt is current and fails the attempt with category `input-unavailable` if it cannot stage before its execution timeout.
- **Revoked Manager**: Refused by authentication; its grants expire within their short lifetime.

## Requirements *(mandatory)*

### Functional Requirements

#### Read operation

- **FR-001**: The platform MUST let the Manager that owns a current, unfenced attempt read the accepted results of one of the attempt's job's declared upstream capabilities, at the schema version the job's dependency pins, optionally narrowed to segments or a match-time range, in pages.
- **FR-002**: The response MUST be a stable, versioned envelope containing, per accepted result, its identity, capability and schema version, segment or match scope, the manifest exactly as validated at acceptance, and one read grant per artifact; and, per expected upstream segment within the requested range, a coverage entry stating accepted, missing, failed, blocked, or skipped. Results and coverage MUST be exactly those recorded in the job's input snapshot when the job became ready, so results accepted later are not included and the read equals what acceptance checks as lineage.
- **FR-003**: Read grants MUST permit reading exactly one stored object of the accepted result and MUST expire no later than the attempt's current lease and the stamp's configured read-grant lifetime, whichever is earlier.
- **FR-004**: The platform MUST refuse reads for attempts that are fenced, completed, or owned by another Manager, for capabilities that are not declared upstream dependencies of the job, and for schema versions other than the pinned one, disclosing nothing; it MUST never return results of another run, match, or team, or results that were not accepted. Revoked or ended registrations MUST be refused by the existing Manager authentication; unregistering Managers MUST still read for their current attempts.
- **FR-005**: Pages MUST be stable: following the continuation returns every matching accepted result exactly once.

#### Staging by the Manager

- **FR-006**: Before launching an attempt whose job declares upstream dependencies, the Analyst Manager MUST read every upstream dependency, download each accepted result's manifest and artifacts, verify each object's digest against the accepted manifest checksum and the manifest's artifact digests, and place them with the coverage into the attempt's input mount.
- **FR-007**: The Analyst MUST receive no platform credentials, grants, or network access to the platform; it reads upstream results only from its input mount.
- **FR-008**: When staging cannot complete (digest mismatch, the staged data would exceed the configured per-attempt staging limit, the host lacks free space for it, storage unavailable until the execution timeout, refused read), the Manager MUST NOT launch the Analyst and MUST report the attempt as failed with the completion contract's failure category (`input-unavailable` for a digest mismatch, unavailable storage, or a refused read; `resource-exhausted` for the staging limit or free space) and record the precise staging reason in its local diagnostics, except for refusals because the attempt is obsolete, which stop the attempt without a completion. The Manager MUST check the total size against the limit and the host's free space before downloading.
- **FR-009**: The Manager MUST renew read grants by reading again while the attempt is current when grants expire during staging.

#### Contract, simulated Analyst, and verification

- **FR-010**: A versioned read-envelope contract MUST exist in the repository's machine-readable contracts with valid and invalid examples and contract tests, used by the platform and the Manager independently, forbidding credentials other than the read grants and any data outside the defined fields.
- **FR-011**: The simulated Analyst MUST, for match-level jobs, build its manifest's lineage from the staged upstream results and record the coverage it consumed, and MUST be configurable to ignore the staged inputs for testing lineage rejection.
- **FR-012**: An automated end-to-end test across the platform and Manager solutions MUST run the development workflow and show that the match-level result's lineage equals the staged segment-level results, and that ignoring the staged inputs leads to a lineage rejection.

#### Audit and observability

- **FR-013**: The platform MUST record a security audit event, without grant addresses or signatures, for every read refused because the attempt belongs to another Manager or because the capability or run is outside the job's scope; successful reads and issued read grants MUST be counted and logged with the attempt identity, not audited individually.

#### Host cleanup

- **FR-014**: The Manager MUST delete an attempt's staged upstream data as soon as the attempt ends, whether completed, failed, fenced, or timed out, and MUST remove staged data left over from a previous process when it starts.

### Key Entities

- **Upstream read query**: Attempt, upstream capability and schema version, optional segments or match-time range, page continuation.
- **Read envelope**: Versioned response with accepted results (identity, capability and schema version, scope, validated manifest, artifact read grants), coverage entries, and the continuation.
- **Coverage entry**: For one expected upstream segment (or the match), its state: accepted, missing, failed, blocked, or skipped.
- **Read grant**: Short-lived permission to read one stored object of an accepted result.
- **Staged inputs**: The manifests, artifacts, and coverage the Manager places in an attempt's input mount before launch.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end test, 100% of match-level attempts start with every accepted upstream result staged with verified digests, and their accepted lineage equals the staged results.
- **SC-002**: For each refusal case of User Story 1, scenario 5, an automated test shows 0 results or grants disclosed.
- **SC-003**: Coverage in the tests reports every expected upstream segment exactly once with the state recorded in the job's input snapshot, and a result accepted after the job became ready is never returned.
- **SC-004**: 0 read grants outlive the lease current when they were issued, and 0 credentials or grants reach the Analyst, in the tests.
- **SC-005**: A staged artifact whose digest does not match causes 0 Analyst launches and exactly one failed completion with category `input-unavailable`.
- **SC-007**: 0 bytes of staged upstream data remain on the host after an attempt ends, and 0 after a Manager restart following a crash, in the tests.
- **SC-006**: Every example of the read-envelope contract validates or fails exactly as its expectation states, for the platform and the Manager.

## Assumptions

- **Actors**: The Analyst Manager reads and stages; the platform serves accepted results; the (simulated) Analyst consumes the input mount. No person acts in this slice.
- **Readiness**: The durable analysis workflow publishes a match-level job only after its required upstream barrier is met, so reads normally find the required results; coverage reports everything else.
- **Grant shape**: Read grants follow the platform's existing direct-to-storage pattern; the platform never relays result bytes.
- **Configured limits**: Read-grant lifetime, page size limits, and the Manager's per-attempt staging limit (development value 2 GiB) are explicit configuration with development and test values; production values remain unresolved under the production operations architecture.
- **Architecture change**: The architecture describes the Analyst retrieving upstream data through the API. With the decision that Analysts receive no platform credentials, the Manager reads and stages on the Analyst's behalf; the plan updates `analysts-models-and-hardware.md` § Capability-Scoped Result Access and the high-level part of the `job-processing.md` sequence in the same change, as the constitution requires.
- **Out of scope**: presigned source-video access for Analysts; fact indexing and any query by people or agents; the Python Analyst SDK and real capabilities; reads across runs; production grant lifetimes and limits.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/20261013-125502-analysis-result-manifests` (Analysis Result Manifests and Validated Acceptance) provides stored manifests and artifacts, accepted manifest records, the result-manifest contract, and the simulated Analyst's manifest writing. `specs/20261012-141233-manager-work-execution` (Analyst Manager Work Execution) provides attempts, the Manager's attempt runner and simulated runtime, and the cross-solution end-to-end test. `specs/archive/20261005-130703-durable-analysis-workflow` provides runs, dependency edges, job input snapshots, and coverage states.
- **Architecture References**: [docs/architecture/analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md) (Capability-Scoped Result Access; Result Manifest And Lineage), [docs/architecture/job-processing.md](../../docs/architecture/job-processing.md) (Match Processing sequence; dependency evaluation and coverage), [docs/architecture/analyst-runtime-and-recovery.md](../../docs/architecture/analyst-runtime-and-recovery.md) (OCI Container Runtime Boundary; launch defaults), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md).

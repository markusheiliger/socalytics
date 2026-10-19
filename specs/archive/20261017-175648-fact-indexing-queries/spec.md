# Feature Specification: Accepted Fact Indexing and Capability-Scoped Fact Queries

**Feature Branch**: `20261017-175648-fact-indexing-queries`

**Created**: 2026-10-17

**Status**: Draft

**Input**: User description: "Accepted Fact Indexing and Capability-Scoped Fact Queries: today the platform validates and accepts Analyst result manifests (Analysis Result Manifests and Validated Acceptance) and lets Managers read a job's accepted upstream results (Analysis Result Read for Jobs), but accepted facts are not indexed and people cannot query them; the architecture names fact indexing as future work. When a result is accepted, the platform validates the result's fact artifacts against the output schema its capability declares and indexes each foundational fact or soccer concept (half-open time range inside the result's validity window, capability id and version, output schema version, result and run identity, confidence, and the capability-owned payload) in stamp metadata, atomically with acceptance or through a durable, idempotent follow-up that never exposes partially indexed results. Authorized members (Club Admins, and Coaches and Viewers of the owning team) read a run's indexed facts through member-facing, match-scoped, team-authorized /api/v1 operations: by capability and schema version, optional match-time range, paginated in stable time order, with explicit per-segment coverage (accepted, missing, failed, blocked, skipped) and provenance (run, workflow, Analyst profile, image and model digests), hiding storage layout and segment boundaries; results of different runs are never mixed, and unavailable or incomplete analysis is reported explicitly rather than inferred. The simulated Analyst of the development workflow emits representative facts so tests and end-to-end evidence exercise indexing and reads. Out of scope: deriving concepts from raw facts, real Analyst capabilities, MCP tools and agents, the Web UI (next spec in the batch), video clips and presigned media, result retention and deletion, cross-run comparison, full-text search, production limits. Depends on Analysis Result Manifests and Validated Acceptance, Analysis Result Read for Jobs, Durable Analysis Workflow, Analyst Registry, Club and Identity Foundation (all merged; archived under specs/archive/). Implements docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope (indexing and the match-wide ordered fact stream), docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage (fact indexing, currently future) and #capability-scoped-result-access, and docs/architecture/contracts-and-compatibility.md for any new contract artifact under contracts/analysis/."

## Clarifications

### Session 2026-10-17

- Q: How deeply should the platform validate a fact's capability-owned payload? → A: Envelope only: the platform validates the fact envelope and requires each payload to be a JSON object within the size limit; per-capability payload schemas are not validated in this feature.
- Q: Should indexing happen in the same step as acceptance or as a durable follow-up after it? → A: In the same step: facts are validated before acceptance and stored in the acceptance's unit of work, so an invalid fact artifact fails the attempt and no partly indexed result can exist.
- Q: Which facts does a match-time range select? → A: Every fact whose half-open range overlaps the requested half-open range.
- Q: What development limits apply to fact artifacts and reads? → A: Fact artifact at most 16 MiB, at most 50,000 facts per result, payload at most 16 KiB, page size default 100 and at most 500; all configurable.
- Q: Must every fact carry its own confidence? → A: Yes: each fact requires a confidence between 0 and 1; the result's overall confidence stays in the manifest.
- Q: Should the Analyst Manager also check fact artifacts against the fact contract before uploading them? → A: No: the platform alone validates fact artifacts at acceptance; the Manager keeps checking only the result manifest, and only its simulated Analyst changes.

## User Scenarios & Testing *(mandatory)*

The actors are the club members who look at analysis (Club Admins, and Coaches and Viewers of the team that owns a match), the platform that accepts and indexes Analyst results, and the Analysts whose accepted results carry the facts. The design follows [Segment And Match Analysis Scope](../../../docs/architecture/match-data-pipeline.md#segment-and-match-analysis-scope), [Result Manifest And Lineage](../../../docs/architecture/analysts-models-and-hardware.md#result-manifest-and-lineage), and [Capability-Scoped Result Access](../../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access).

### User Story 1 - Accepted results become queryable facts (Priority: P1)

When an Analyst's result is accepted, every fact or soccer concept it reports is checked and stored as an individual, queryable record of the analysis run, together with its match-time range, capability, schema version, confidence, and the capability's own payload. A result whose facts do not satisfy the published fact contract, lie outside its validity window, or do not match its declared fact count is not accepted; the attempt fails with a named reason and the normal retry rules apply. No reader ever sees a result whose facts are only partly stored.

**Why this priority**: Without indexing, accepted analysis exists only as opaque stored objects; nothing downstream (people, the Web UI, later agents) can use it. On its own, it guarantees that every accepted result is fully and correctly represented as facts.

**Independent Test**: Complete an attempt with a result whose fact artifact holds a known set of facts and confirm that exactly those facts are stored with the result's identity; complete attempts with a malformed fact artifact, a fact outside the validity window, and a wrong fact count, and confirm each attempt fails with its named reason and no fact of it is stored.

**Acceptance Scenarios**:

1. **Given** a current attempt whose stored result passes all existing acceptance checks and whose fact artifacts satisfy the fact contract, **When** the Manager completes the attempt, **Then** the result is accepted and each of its facts is stored with its half-open match-time range, kind, confidence, payload, capability id and version, output schema version, output type, and the identities of its result, run, node, team, and match, in the same step as the acceptance.
2. **Given** a fact artifact that is not valid against the fact contract, names another capability, capability version, or schema version than the result, or exceeds the configured size limits, **When** the attempt completes, **Then** the attempt fails with a named reason, the result is not accepted, and no fact of it is stored.
3. **Given** a fact whose range is empty or extends outside the result's half-open validity window, **When** the attempt completes, **Then** the attempt fails with a named reason and no fact of it is stored.
4. **Given** a result whose fact artifacts together hold a different number of facts than the manifest's declared fact count, **When** the attempt completes, **Then** the attempt fails with a named reason and no fact of it is stored.
5. **Given** a result that declares no facts and carries no fact artifact, **When** it is accepted, **Then** it is accepted as today and contributes no facts.
6. **Given** a completion that is retried with the same retry key, or acceptance that fails part-way, **When** acceptance completes, **Then** each fact is stored exactly once, and a reader sees either none or all of the result's facts.

---

### User Story 2 - Members read a run's facts by capability (Priority: P1)

A Coach opens a finished analysis run of a match and asks for the facts of one capability and schema version, optionally limited to a match-time range. The platform returns those facts page by page in a stable match-time order, each with its time range, confidence, payload, and the identity of the result it came from, together with the run's provenance for that capability (workflow, Analyst profile, image and model digests) and an explicit coverage statement that says for each expected part of the match whether analysis was accepted, is missing, failed, is blocked, or was skipped. The response never reveals where results are stored or how the match was cut into segments, and never mixes facts of different runs.

**Why this priority**: This is the first time a person can see what the analysis found. It needs indexing (Story 1) and is the basis for the Web UI review in the next feature.

**Independent Test**: For a run with accepted results of one capability across several segments, a Coach of the team reads the capability's facts and receives every fact exactly once across pages in time order, plus coverage covering the whole match; a member of another team, or one without a role on the team, receives "not found".

**Acceptance Scenarios**:

1. **Given** a run with indexed facts, **When** a Club Admin, or a Coach or Viewer of the owning team, reads one capability, capability version, and schema version of that run, **Then** the platform returns the run's facts of that capability ordered by start, end, and a stable tie-breaker, each with its half-open match-time range, kind, confidence, payload, and result identity.
2. **Given** more facts than one page holds, **When** the member follows the continuation, **Then** every fact is returned exactly once across pages in the same order, and a page size outside the allowed bounds is rejected.
3. **Given** a match-time range, **When** the member reads with it, **Then** only facts whose range overlaps the requested half-open range are returned, and coverage is limited to the parts of the match that overlap it.
4. **Given** a capability whose results are accepted for some parts of the match and missing, failed, blocked, or skipped for others, **When** the member reads, **Then** the response states the coverage for every expected part of the match as a half-open match-time window with one of `accepted`, `missing`, `failed`, `blocked`, or `skipped`, without segment, recording, or storage identifiers.
5. **Given** a run that is still running, failed, or has no accepted result for the capability yet, **When** the member reads, **Then** the response returns the facts accepted so far and states the run's state and the coverage explicitly; an empty fact list is never presented as "nothing happened" without that coverage.
6. **Given** a capability, capability version, or schema version that the run's workflow does not contain, **When** the member reads, **Then** the platform answers with a distinct problem that says the run does not include it.
7. **Given** a member without a role on the owning team, a member of another club, a run that does not exist, or a request without a valid session, **When** the member reads, **Then** the platform answers "not found" (or "unauthorized" without a session) and discloses nothing.
8. **Given** two runs of the same match, **When** the member reads one of them, **Then** only that run's facts and coverage are returned.

---

### User Story 3 - Members see which fact streams a run offers (Priority: P2)

Before reading facts, a member asks which capabilities of a run have facts to show. The platform lists, for every capability node of the run, its capability id and version, output schema version, output type, the number of indexed facts, the provenance of its accepted results, and a coverage summary, so a client can choose a stream without guessing.

**Why this priority**: It makes the fact read discoverable for clients such as the Web UI review of the next feature; the read itself (Story 2) already works when the caller knows the capability.

**Independent Test**: For a run of the development workflow, a Viewer of the team lists the run's fact streams and receives one entry per capability node with its fact count and coverage summary, matching what the fact read returns for each.

**Acceptance Scenarios**:

1. **Given** a run, **When** an authorized member lists its fact streams, **Then** the platform returns one entry per capability node of the run's workflow with capability id and version, schema version, output type, indexed fact count, provenance, and coverage counts per state.
2. **Given** a capability node without accepted results, **When** the member lists the streams, **Then** its entry shows zero facts and coverage that is entirely missing, failed, blocked, or skipped, as the run's state says.
3. **Given** an unauthorized caller, **When** it lists the streams, **Then** the platform answers as for the fact read and discloses nothing.

---

### User Story 4 - The development workflow produces representative facts (Priority: P3)

The simulated Analyst of the development workflow writes a fact artifact with a small, deterministic set of representative facts inside each job's validity window, so that development, integration, and end-to-end tests exercise indexing and the member reads without real Analyst capabilities.

**Why this priority**: It provides the executable evidence for Stories 1 to 3 across the platform and the Analyst Manager; it adds no user-facing capability on its own.

**Independent Test**: Run the development workflow end to end with the simulated Analyst and confirm that every accepted result's facts are indexed and returned by the member read with full coverage.

**Acceptance Scenarios**:

1. **Given** the simulated Analyst in its valid result mode, **When** it completes a segment-level or match-level job, **Then** its result carries a fact artifact with a deterministic set of facts inside the job's validity window and a fact count equal to their number, and the result is accepted and indexed.
2. **Given** the simulated Analyst configured to produce an invalid fact artifact, **When** it completes a job, **Then** the attempt fails with the named fact-validation reason.
3. **Given** a completed run of the development workflow, **When** the end-to-end test reads each capability's facts as a member of the team, **Then** every fact of every accepted result is returned exactly once with accepted coverage for the whole match.

### Edge Cases

- A fact whose range starts exactly at the validity window's end, or ends after it, is outside the window and rejects the result; a fact ending exactly at the window's end is inside.
- Two facts with identical ranges are ordered by their result identity and their position among the result's facts, so pagination stays stable.
- A requested range with an end not after its start, a negative bound, or a malformed continuation is rejected as a bad request.
- A continuation obtained for one run, capability, schema version, or range is rejected when used with another.
- A soccer concept of a match-level result may span many segments; it is returned once with its full range, and coverage for match-level capabilities is a single window for the whole match.
- A result that declares no facts but carries fact artifacts with facts, or declares facts but carries no fact artifact, fails the count check.
- Facts of results accepted before this feature (which have no fact artifact or none in the new format) are not indexed retroactively; their capability reads show accepted coverage with no facts, and the stream listing shows a fact count of zero with accepted coverage.
- A workflow that contains the same capability version in more than one node requires the caller to name the node; without it the read is rejected as ambiguous.
- A stored fact artifact that cannot be read during acceptance because storage is unavailable is treated like today's unavailable manifest: acceptance is retried, not failed.
- Payloads are returned exactly as accepted; the platform never interprets or rewrites capability-owned payload fields.

## Requirements *(mandatory)*

### Functional Requirements

#### Fact contract and validation

- **FR-001**: The platform MUST publish a versioned fact-artifact contract in `contracts/analysis/` that defines the platform-owned envelope of a fact artifact: contract version, capability id and version, output schema version, output type, and an ordered list of facts, each with a capability-owned kind label, a half-open match-time range in integer milliseconds, a required confidence between 0 and 1, and a capability-owned payload that MUST be a JSON object; the platform validates the envelope and the payload's size but not the payload's capability-specific fields.
- **FR-002**: A result artifact MUST be recognized as a fact artifact by a dedicated media type declared in the result manifest; other artifacts remain opaque supporting artifacts and are not indexed.
- **FR-003**: During acceptance, the platform MUST read every fact artifact of the result, verify its digest as today, and validate it against the fact-artifact contract, including that its capability id and version, schema version, and output type equal those of the result manifest and the run's pinned node snapshot. The platform is the only component that validates fact artifacts; the Analyst Manager's pre-upload check stays limited to the result manifest.
- **FR-004**: The platform MUST reject the result when any fact has an empty range or a range outside the result's half-open validity window, when the total number of facts differs from the manifest's declared fact count, or when a fact artifact, a payload, or the number of facts exceeds the configured limits (development defaults: 16 MiB per fact artifact, 16 KiB per serialized payload, 50,000 facts per result).
- **FR-005**: A rejected result MUST end the attempt as failed with a named reason, distinct from the existing manifest reasons, and the normal retry rules MUST apply; nothing of the rejected result is stored as facts.

#### Indexing

- **FR-006**: For every accepted result, the platform MUST store each fact as an individual record with its half-open match-time range, kind, confidence, payload, position among the result's facts, capability id and version, output schema version, output type, and the identities of its accepted result, run, node, team, and match.
- **FR-007**: Fact validation MUST complete before the result is accepted, and indexing MUST be stored in the same unit of work as the acceptance, so that a result is either accepted with all of its facts stored or not accepted with none stored; no durable follow-up step is used, and readers MUST never see a partially indexed result.
- **FR-008**: Indexing MUST be idempotent: a repeated or retried completion of the same attempt MUST NOT store a fact twice or change stored facts.
- **FR-009**: Stored facts MUST be immutable once stored.
- **FR-010**: Results accepted before this feature MUST NOT be re-read or indexed retroactively.

#### Member reads

- **FR-011**: The platform MUST provide a member-facing `/api/v1` read of a run's facts for one capability id, capability version, and output schema version, with an optional half-open match-time range, a page size (configurable; development default 100, at most 500), and an opaque continuation.
- **FR-012**: The read MUST return facts in a stable order (start, then end, then accepted-result identity, then position among the result's facts) and every fact of the selection exactly once across pages.
- **FR-013**: With a range, the read MUST return exactly the facts whose range overlaps the requested half-open range.
- **FR-014**: Each response MUST state the run's identity, match, and state, and the capability's provenance: workflow id and version, Analyst profile id and digest, image digest, and model digest where present, as pinned in the run's node snapshot.
- **FR-015**: Each response MUST report coverage for every expected execution unit of the capability's node (each expected segment, or the whole match for match-scope nodes) that overlaps the selection, as a half-open match-time window with the state `accepted` (the unit has an accepted result), `failed` or `blocked` (its job failed or is blocked), `skipped` (the node is not required by the run's requested capabilities), or `missing` (otherwise), derived from the run's current state.
- **FR-016**: Responses MUST NOT expose storage locations, object names, segment identities, recording versions, timeline-mapping or policy digests, or other storage or segmentation identifiers.
- **FR-017**: A read MUST only return facts of the requested run; facts of other runs of the same match MUST never be returned or merged.
- **FR-018**: The platform MUST provide a member-facing `/api/v1` listing of a run's fact streams with one entry per capability node of the run's workflow: node key, capability id and version, schema version, output type, indexed fact count, provenance, and coverage counts per state.
- **FR-019**: A capability, capability version, or schema version the run does not contain MUST be answered with a distinct problem; when the run contains it in more than one node, the read MUST require the node key and reject the request as ambiguous without it.
- **FR-020**: Reads MUST be authorized as reads of the run's team: Club Admins of the club and Coaches and Viewers of the owning team are allowed; every other member, and every missing or foreign run, MUST receive "not found"; requests without a valid member session MUST be rejected as unauthorized.
- **FR-021**: Invalid parameters (page size out of bounds, malformed or mismatched continuation, a range whose end is not after its start, negative bounds) MUST be rejected with a validation problem.
- **FR-022**: Fact reads and stream listings MUST be published in the OpenAPI document of the API and, as views over several records, return a weak validator usable for conditional requests.

#### Development evidence

- **FR-023**: The simulated Analyst MUST write, in its valid result mode, a fact artifact with a deterministic set of representative facts inside the job's validity window and the matching fact count, and MUST offer a configurable mode that writes an invalid fact artifact.
- **FR-024**: Automated tests MUST cover fact-artifact validation and its rejections, atomic and idempotent indexing, the member reads with ordering, pagination, ranges, coverage, provenance, run isolation, and authorization, and an end-to-end run of the development workflow whose facts are read back by a member.

### Key Entities *(include if feature involves data)*

- **Fact Artifact**: A result artifact of the dedicated fact media type that follows the fact-artifact contract; it holds the facts of one result for one capability and schema version.
- **Indexed Fact**: One immutable fact or soccer concept of an accepted result: half-open match-time range, kind, confidence, capability-owned payload, its position among the result's facts, and the identities of its accepted result, run, node, team, and match, with capability id and version, schema version, and output type.
- **Fact Stream**: The indexed facts of one capability node of one run, identified by capability id and version and output schema version (and node key when ambiguous), with its provenance and coverage.
- **Coverage Window**: The state of one expected execution unit of a capability node, expressed as a half-open match-time window and one of `accepted`, `missing`, `failed`, `blocked`, or `skipped`.
- **Accepted Result** (existing): The accepted result and its validated manifest; indexing adds its facts.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In automated tests, 100% of facts in accepted results are stored exactly once, and 0 facts of rejected results are stored.
- **SC-002**: In automated tests, every fact-validation failure case (contract violation, capability or schema mismatch, range outside the window, count mismatch, limit exceeded) ends the attempt with its named reason in 100% of runs.
- **SC-003**: Reading a stream page by page returns every fact of the selection exactly once in stable time order across at least three pages in automated tests.
- **SC-004**: For every run in automated tests, coverage accounts for 100% of the capability's expected execution units, and no response contains storage or segmentation identifiers.
- **SC-005**: Members without a role on the owning team receive "not found" for 100% of fact reads and stream listings in the authorization tests.
- **SC-006**: The end-to-end test of the development workflow reads back every accepted fact of the run as a member of the team, with accepted coverage for the whole match.

## Assumptions

- The platform owns only the fact envelope; the payload of each fact is a capability-owned JSON object that the platform size-checks, stores, and returns unchanged. Validating payloads against per-capability payload schemas requires capability schema publication, which belongs to the Model/Capability Registry and later capability features.
- Fact ranges use integer milliseconds of match time, as the result manifest's validity window does; instantaneous events use a minimal non-empty range.
- Size and count limits (fact-artifact size, payload size, facts per result, page size) are configurable, with the development defaults named in FR-004 and FR-011; production limits are out of scope.
- Fact reads are not security-audited, consistent with the existing analysis-run reads.
- Out of scope: deriving concepts from raw facts, real Analyst capabilities and the Python Analyst SDK, MCP tools and agents, the Web UI (the next feature, Web UI Connected Analysis Review, builds on these reads), video clips and presigned media, result retention and deletion, cross-run comparison, full-text search, and production limits.
- Dependencies: [Analysis Result Manifests and Validated Acceptance](../20261013-125502-analysis-result-manifests/spec.md), [Analysis Result Read for Jobs](../20261013-134112-analysis-result-read/spec.md), [Durable Analysis Workflow](../20261005-130703-durable-analysis-workflow/spec.md), [Analyst Registry](../20261012-152148-analyst-registry/spec.md), and [Club and Identity Foundation](../20261005-130701-club-identity-foundation/spec.md), all merged. No new framework, SDK, or tool is needed.

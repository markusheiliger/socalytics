# Feature Specification: Analysis Result Manifests and Validated Acceptance

**Feature Branch**: `20261013-125502-analysis-result-manifests`

**Created**: 2026-10-13

**Status**: Draft

**Input**: User description: "Analysis Result Manifests: give accepted analysis results real content and provenance, still with the simulated Analyst from Analyst Manager Work Execution. (1) Result write grants: for a claimed, unfenced attempt, the Manager requests short-lived write grants for that attempt's result location in stamp-local object storage (manifest and artifacts), scoped to the job and attempt, never outliving the lease, refused after fencing or revocation; results of fenced attempts are never accepted. (2) A versioned result-manifest contract in contracts/analysis/result-manifest/v1/ (JSON Schema 2020-12 with valid and invalid examples) with the fields the architecture names: team, match, recording-set version, segment identities and windows or full-match scope, capability, Analyst profile, model or policy, output schema, workflow and image versions and digests, attempt identity, result address and checksum, validity window and fact count, complete upstream lineage, output type, confidence, and evidence references. (3) Acceptance: on a successful completion the platform reads the manifest from storage within a size limit, verifies its checksum, schema, team, match, run, node and segment scope, pinned digests, declared dependencies and complete upstream lineage, and the validity window, and accepts only then; otherwise the completion fails with a reason and the normal retry rules apply. The simulated Analyst writes a small manifest and artifact through the grants, and a cross-solution end-to-end test proves write and acceptance. The capability-scoped result read for jobs follows as its own spec. Out of scope: the Python Analyst SDK and real capabilities, indexing queryable facts and the Web UI analysis review, presigned video access for Analysts, result retention and deletion, production grant lifetimes and size limits. Depends on Analyst Manager Work Execution and Durable Analysis Workflow. Implements docs/architecture/analysts-models-and-hardware.md (Result Manifest And Lineage; the JSON Schema statement of Implementation Profile), docs/architecture/job-processing.md (result write and acceptance), and docs/architecture/analyst-runtime-and-recovery.md (short-lived, narrowly scoped object-storage credentials)."

## Clarifications

### Session 2026-10-13

- Q: What happens to the Logical Job when the platform rejects a manifest during acceptance? → A: The attempt fails and the job is retried while its attempt budget lasts, like any explicitly failed attempt; there is no separate non-retryable class in this slice.
- Q: What does the Analyst Manager do when its own check finds the Analyst's manifest violates the contract? → A: It checks before requesting grants; if invalid, it uploads nothing and reports a failed completion with the completion contract's category `analyst-error`, keeping the violation in its local diagnostics (refined at analysis of the follow-up read spec, because the completion contract carries only sanitized categories). Platform-side contract rejections are tested through direct calls; scope, provenance, lineage, artifact, and validity rejections through the simulated Analyst.
- Q: Which grant and acceptance events does the platform record for security review? → A: Security audit events for refused grant requests that indicate misuse (another Manager's attempt, a revoked or inactive Manager) and for every acceptance rejection, without secret values; issued grants are counted in metrics and logged, not audited individually. (Refined at analysis: revoked or ended registrations are already refused and audited by the existing Manager authentication, so the grant operation audits refusals for another Manager's attempt; unregistering Managers keep obtaining grants for their current attempts.)

## User Scenarios & Testing *(mandatory)*

The actors are machines: the Analyst Manager running an attempt (with the simulated Analyst), the platform that accepts results, and the operators and developers who rely on accepted results being trustworthy evidence for later analysis.

### User Story 1 - Write an attempt's results to object storage (Priority: P1)

An Analyst Manager has claimed an attempt and its (simulated) Analyst has produced a result. The Manager asks the platform for permission to store the result: it names each object it wants to write (the manifest and one or more artifacts) with its size and content digest. The platform issues short-lived write grants for exactly those objects under a location that belongs to this attempt alone. The Manager stores the bytes directly in object storage; the platform never relays result bytes.

**Why this priority**: Without a place to put results, nothing can be accepted with real content. On its own, this story already proves that results land in stamp-local storage under the right scope, with integrity enforced by storage.

**Independent Test**: For a claimed attempt, request grants for a manifest and an artifact, store both with the grants, and confirm they exist under the attempt's location with the declared sizes and digests; request grants for a fenced attempt, for another Manager's attempt, and after revocation, and observe the refusals.

**Acceptance Scenarios**:

1. **Given** a Manager that holds a claimed, unfenced attempt, **When** it requests write grants for a manifest and artifacts with their sizes and content digests, **Then** the platform returns one short-lived grant per object, each limited to that object's name under the attempt's result location, its exact size, and its digest, expiring no later than the attempt's current lease.
2. **Given** a grant, **When** the Manager stores bytes whose size or digest differ from the declaration, or a different object name, **Then** storage refuses the write and nothing is stored.
3. **Given** an attempt that was fenced, completed, or belongs to another Manager, **When** write grants are requested, **Then** the request is refused and no grant is issued; **and Given** a Manager whose registration was revoked or otherwise ended, **When** it requests grants, **Then** its authentication is refused as for any other platform call; a Manager that is unregistering (draining) still obtains grants for its own current attempts.
4. **Given** grants that expire during a long write, **When** the Manager requests grants again for the remaining objects while the attempt is still current, **Then** fresh grants for the same objects are issued.
5. **Given** a request for more objects or more total bytes than the stamp's configured limits allow, **When** it is submitted, **Then** it is refused as a whole and no grant is issued.
6. **Given** an object that was already stored for this attempt, **When** the Manager requests a grant for the same name with a different digest, **Then** the request is refused, so a stored result object cannot be replaced.

---

### User Story 2 - Accept a result only after validating its manifest (Priority: P2)

When the Manager completes the attempt successfully, it reports the manifest's location and checksum, as today. The platform now reads the manifest from object storage and checks it before accepting the result: the checksum matches, the manifest is valid against the versioned manifest contract, it describes exactly this attempt's team, match, run, node, recording-set version, segment or match scope, capability, schema, Analyst profile, image, and model, it lists complete upstream lineage matching the job's accepted inputs, every artifact it references exists under the attempt's location with the stated digest, and its validity window and fact count are consistent. Only then is the result accepted. A manifest that fails any check turns the completion into a failed attempt with a precise reason, and the platform's normal retry rules decide what happens next.

**Why this priority**: Acceptance is what makes a result trustworthy evidence that later analysis and people may rely on. It needs stored results (Story 1).

**Independent Test**: Complete one attempt with a valid manifest and observe one accepted result referencing it; complete attempts whose manifests have a wrong checksum, an invalid shape, a foreign match, a different image digest, missing lineage, or a missing artifact, and observe failed attempts with the matching reason and no accepted result.

**Acceptance Scenarios**:

1. **Given** a current attempt whose manifest and artifacts are stored and valid, **When** the Manager reports a successful completion with the manifest's location and checksum, **Then** the platform accepts exactly one result for the attempt, records the manifest's location, checksum, and contract version, and the job's dependents can become ready as before.
2. **Given** a manifest whose stored checksum differs from the reported one, that exceeds the configured maximum manifest size, or that is missing, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with a reason that names the problem.
3. **Given** a manifest that is not valid against the manifest contract version it declares, or declares a version the platform does not support, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with a schema reason.
4. **Given** a manifest whose team, match, run, node, recording-set version, segment or match scope, capability, schema, Analyst profile, image, or model differs from the attempt's pinned job, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with a scope or provenance reason.
5. **Given** a manifest whose upstream lineage omits, adds, or misstates any accepted upstream result the job was given, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with a lineage reason.
6. **Given** a manifest that references an artifact outside the attempt's location, an artifact that was not stored, or an artifact whose stored digest differs, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with an artifact reason.
7. **Given** a manifest whose validity window falls outside the job's segment or match window, or whose fact count is negative, **When** the completion arrives, **Then** no result is accepted and the attempt ends as failed with a validity reason.
8. **Given** a completion that was already processed, **When** it is retried with the same idempotency key, **Then** the original outcome is returned and nothing is validated or accepted twice.
9. **Given** an attempt that was fenced after its results were written, **When** its completion arrives, **Then** it is rejected as obsolete as today, and its stored objects never become an accepted result.

---

### User Story 3 - The simulated Analyst produces real result manifests (Priority: P3)

The simulated Analyst of Analyst Manager Work Execution now writes a small, deterministic manifest and one artifact for every successful attempt, through the grants of Story 1, so that the development workflow runs end to end with validated acceptance. It can also be configured to produce invalid manifests, so the rejections of Story 2 can be exercised with the real Manager; contract violations are stopped by the Manager itself before anything is uploaded.

**Why this priority**: It makes the whole chain demonstrable and keeps the development workflow working once acceptance requires manifests. It needs Stories 1 and 2.

**Independent Test**: Run the development workflow with the simulated Analyst against the platform; observe accepted results for both workflow nodes, with manifests and artifacts in object storage and the match-level node's lineage naming the segment-level results; configure an invalid-manifest outcome and observe the failed attempt and its retry.

**Acceptance Scenarios**:

1. **Given** the development workflow and the simulated Analyst, **When** a recording set is finalized, **Then** every job completes with a stored, valid manifest and artifact, and every result is accepted.
2. **Given** a match-level job whose segment-level dependencies were accepted, **When** its simulated Analyst writes the manifest, **Then** the manifest's lineage names exactly those accepted upstream results with their capability and schema versions and digests, and acceptance succeeds.
3. **Given** the simulated Analyst configured to write a contract-valid manifest that violates scope, provenance, lineage, artifact, or validity, **When** the attempt completes, **Then** the platform fails it with the matching reason, and the job is retried or failed according to its attempt budget; **and Given** it is configured to write a contract-invalid manifest, **When** the attempt ends, **Then** the Manager uploads nothing and reports a failed completion with category `analyst-error`, recording the contract violation in its local diagnostics.
4. **Given** a Manager whose attempt is fenced while it writes results, **When** it tries to request further grants or complete, **Then** it is refused, stops the attempt, and sends nothing further, as the Manager already does for fenced attempts.

### Edge Cases

- **Completion without stored manifest**: The Manager reports success but never stored the manifest; acceptance fails with a missing-manifest reason.
- **Object storage unavailable during acceptance**: The completion is answered as a temporary failure without changing state, so the Manager retries the identical completion; nothing is accepted from an unverified manifest.
- **Manifest larger than the limit**: Refused at grant time when declared; if a stored manifest exceeds the limit anyway, acceptance fails without reading it completely.
- **Duplicate artifact names in one request**: The request is refused as a whole.
- **Lease renewed during a long write**: Grants never outlive the lease current at issue time; the Manager requests new grants after renewal.
- **Revocation during writing**: Further grant requests are refused at once by the Manager authentication; already-issued grants may still store bytes until they expire, but the attempt is fenced and those objects are never accepted.
- **Unregistering Manager**: While it drains, the Manager still obtains grants and completes its own current attempts; it claims no new work.
- **Abandoned objects of failed or fenced attempts**: They stay unreferenced in storage; their cleanup belongs to result retention, which is out of scope.
- **Manifest declaring a newer contract version**: Refused as unsupported until the platform supports that version; it is never accepted on a best-effort basis.
- **Result address that looks like a presigned URL or embeds credentials**: Refused by the manifest contract.

## Requirements *(mandatory)*

### Functional Requirements

#### Result write grants

- **FR-001**: The platform MUST let the Manager that owns a current, unfenced attempt request write grants for named result objects, each with its exact size and content digest, under a result location that the platform derives from the attempt and that no other attempt shares.
- **FR-002**: Each grant MUST permit storing exactly one object with exactly the declared name, size, and digest, so storage refuses anything else, and MUST expire no later than the attempt's current lease and the stamp's configured grant lifetime, whichever is earlier.
- **FR-003**: The platform MUST refuse grant requests for attempts that are fenced, completed, or owned by another Manager, that exceed the stamp's configured limits on objects per attempt, bytes per object, or bytes per attempt, that repeat an object name within one request, or that name an already declared object with a different digest. Managers whose registration was revoked or otherwise ended MUST be refused by the existing Manager authentication; a Manager that is unregistering (draining) MUST still obtain grants for its own current attempts.
- **FR-004**: The platform MUST NOT receive or relay result bytes; only the Manager writes them to object storage.

#### Result-manifest contract

- **FR-005**: A versioned result-manifest contract MUST exist in the repository's machine-readable contracts with valid and invalid examples and contract tests, covering every field that the architecture's result manifest names (scope, versions and digests, attempt identity, result address and checksum, validity window, fact count, upstream lineage, output type, confidence, evidence references) and forbidding credentials, presigned addresses, and inline media.
- **FR-006**: The platform and the Analyst Manager MUST validate manifests against that contract independently; neither treats the other's types as authoritative.

#### Acceptance

- **FR-007**: On a successful completion, the platform MUST read the manifest from the attempt's result location up to the stamp's configured maximum manifest size and MUST accept the result only when the stored checksum matches the reported one and the manifest is valid against a supported contract version.
- **FR-008**: The platform MUST accept the result only when the manifest's team, match, run, node, recording-set version, segment or match scope, capability and schema versions, Analyst profile, image, and model or policy digests equal the attempt's pinned job, and its workflow identity equals the run's.
- **FR-009**: The platform MUST accept the result only when the manifest's upstream lineage names exactly the accepted upstream results the job received, with their capability and schema versions and digests.
- **FR-010**: The platform MUST accept the result only when every artifact the manifest references lies within the attempt's result location and was stored with the stated digest, as attested by storage.
- **FR-011**: The platform MUST accept the result only when the manifest's validity window lies within the job's segment window or match scope and its fact count is not negative.
- **FR-012**: When any check fails, the platform MUST NOT accept a result and MUST end the attempt as failed with a reason that names the failed check, so the existing retry rules for explicitly failed attempts apply unchanged (the job is retried while its attempt budget lasts; no rejection is treated as non-retryable); when storage is temporarily unavailable, the platform MUST answer with a temporary failure and change nothing.
- **FR-013**: Accepted results MUST record the manifest location, checksum, contract version, and the validated provenance, and MUST remain immutable; repeated completions MUST return the original outcome without validating or accepting again.

#### Simulated Analyst and Manager

- **FR-014**: The Analyst Manager MUST check a successful attempt's manifest against the result-manifest contract before requesting grants; when it is invalid, the Manager MUST upload nothing and report a failed completion with the attempt-completion contract's category `analyst-error`, recording the violation (`result-manifest-schema-invalid`) in its local diagnostics. Otherwise it MUST request grants and store the manifest and artifacts before completing, and MUST stop the attempt without completing when grants are refused for a fenced or revoked attempt.
- **FR-015**: The simulated Analyst MUST write a deterministic manifest and one artifact for every successful attempt, with lineage taken from the job's accepted inputs, and MUST be configurable per capability to produce a contract-valid manifest that violates scope, provenance, lineage, artifact, or validity, and a contract-invalid manifest; platform-side contract rejections (User Story 2, scenario 3) are verified through direct calls, because the Manager stops contract-invalid manifests first.

#### Verification

- **FR-016**: An automated end-to-end test across the platform and Manager solutions MUST run the development workflow with the simulated Analyst against disposable infrastructure, including object storage, and show accepted results with stored manifests for both nodes and a rejected invalid manifest with its retry.

#### Audit and observability

- **FR-017**: The platform MUST record a security audit event, without grant addresses, signatures, or other secret values, for every grant request refused because the attempt is owned by another Manager and for every acceptance rejection with its reason; refusals of revoked or ended registrations are recorded by the existing Manager authentication audit; issued grants MUST be counted and logged with the attempt identity but not audited individually.

### Key Entities

- **Result location**: The attempt-specific place in stamp-local object storage under which a Manager may store that attempt's manifest and artifacts.
- **Result write grant**: A short-lived permission to store one declared object (name, size, digest) under a result location.
- **Result manifest**: The versioned, immutable description of one attempt's result: scope, provenance (versions and digests), attempt identity, artifacts with digests, validity window, fact count, upstream lineage, output type, confidence, and evidence references.
- **Accepted result**: The platform's record that one attempt's manifest passed validation, with the manifest location, checksum, contract version, and provenance; immutable.
- **Acceptance failure reason**: The named check that failed (checksum, size, schema, scope, provenance, lineage, artifact, validity, missing manifest).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the end-to-end test, 100% of the development workflow's jobs complete with a stored, valid manifest and an accepted result, and the match-level result's lineage names exactly its segment-level inputs.
- **SC-002**: For each acceptance check of User Story 2, an automated test shows 0 accepted results and exactly the matching failure reason for a manifest that violates only that check.
- **SC-003**: 0 result bytes pass through the platform in the tests; all are stored by the Manager directly in object storage.
- **SC-004**: 0 grants are issued for fenced, completed, foreign, or revoked attempts, 0 grants outlive the lease current when they were issued, and an unregistering Manager completes its current attempts with grants, in the tests.
- **SC-005**: Ten identical concurrent completions of one attempt yield exactly one accepted result and one validation.
- **SC-006**: Every example in the manifest contract validates or fails exactly as its expectation states in the contract tests, for the platform and the Manager.
- **SC-007**: In the tests, every misuse-indicating grant refusal and every acceptance rejection produces exactly one security audit event with its reason, and 0 audit events or logs contain a grant address or signature.

## Assumptions

- **Actors**: The Analyst Manager (with the simulated Analyst) writes results and completes attempts; the platform issues grants and accepts results. No person acts on results in this slice.
- **Grant shape**: Write grants follow the platform's existing direct-to-storage pattern for recordings: storage enforces the declared size and digest, and the platform never sees the bytes.
- **Failure handling**: A manifest that fails validation is treated like an explicitly failed attempt, so the attempt budget and retry rules of the durable analysis workflow decide whether the job is retried; the Manager does not decide.
- **Pinned provenance**: The digests and versions to compare come from the Logical Job and run snapshots of the durable analysis workflow; the Analyst Registry is not required.
- **Configured limits**: Grant lifetime, maximum objects per attempt, maximum bytes per object and per attempt, and maximum manifest size are explicit configuration with development and test values; production values remain unresolved under the production operations architecture.
- **Out of scope**: the capability-scoped result read for jobs (its own follow-up spec), the Python Analyst SDK and real capabilities, indexing queryable facts and any human-facing result view, presigned video access for Analysts, result retention, deletion, and cleanup of abandoned objects, real container runtimes, and production grant lifetimes and limits.
- **Evidence**: Passing validation is development evidence only and does not establish deployment support or production readiness.
- **Dependencies**: `specs/archive/20261012-141233-manager-work-execution` (Analyst Manager Work Execution) provides claims, heartbeats, completion from the Manager, the simulated Analyst, revocation that fences attempts, and the cross-solution end-to-end test this feature extends. `specs/archive/20261005-130703-durable-analysis-workflow` provides runs, Logical Jobs, attempts, completion acceptance, and the attempt-completion contract. `specs/archive/20261005-130702-recording-lineage-upload` provides the object-storage abstraction and the direct-to-storage grant pattern.
- **Architecture References**: [docs/architecture/analysts-models-and-hardware.md](../../docs/architecture/analysts-models-and-hardware.md) (Implementation Profile; Result Manifest And Lineage; Implementation Validation), [docs/architecture/job-processing.md](../../docs/architecture/job-processing.md) (Durable Workflow Model), [docs/architecture/analyst-runtime-and-recovery.md](../../docs/architecture/analyst-runtime-and-recovery.md) (OCI Container Runtime Boundary; Registration And Runtime Credentials), [docs/architecture/contracts-and-compatibility.md](../../docs/architecture/contracts-and-compatibility.md), [docs/architecture/security-and-data-governance.md](../../docs/architecture/security-and-data-governance.md).

# Feature Specification: Capability Fact Payload Schemas

**Feature Branch**: `20261023-010826-fact-payload-schemas`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "Today the platform accepts an Analyst result after checking the fact envelope only; each fact's capability-owned payload is only required to be a JSON object within the size limit. Let an Analyst capability declare, per fact kind, a payload schema in its versioned analyst-manifest, import and pin it with the capability declaration and the run's node snapshot, and have result acceptance validate every fact payload against the pinned schema, so a malformed result ends the attempt as failed with a named reason and no partially indexed result is ever visible. Include the manifest contract change with golden examples shared by the platform and the Python Analyst SDK (SDK contract copy refreshed), SDK support to validate facts before writing them, and a payload-validated variant of the reference-media-probe frame-second-probe fact as end-to-end evidence. Out of scope: any catalog capability, payload interpretation or rewriting on reads (payloads stay returned exactly as accepted), new member endpoints, Web UI changes, and quality metrics."

## Clarifications

### Session 2026-10-23

- Q: Should the Python Analyst SDK check fact payloads against the Analyst's own declared schemas automatically on every result write, or only when the Analyst author explicitly asks for the check? → A: Automatic: whenever the manifest declares payload schemas, the SDK validates every fact before writing the result and always fails the write on a violation.
- Q: Which JSON schema dialect and feature subset must the manifest contract fix for fact payload schemas, so that the platform and the Python SDK give identical verdicts? → A: A restricted, self-contained subset of JSON Schema 2020-12 (`type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, numeric, string, and array bounds, internal `$defs`/`$ref` only), with no external references and no format assertions.
- Q: Should the payload-validated reference-media-probe be a separate, additionally registered variant (new capability version or image) alongside the existing reference capability, or should the existing reference capability's manifest be changed to declare the schema? → A: Keep the existing reference capability version unchanged and add a new capability version (for example 1.1.0) with the declared schema, so both are registered and tested side by side.

## User Scenarios & Testing *(mandatory)*

The users are Analyst authors, who need to state what the facts of their capability look like and to find mistakes
before a result leaves their container; Club Admins, who register Analyst images and rely on the Registry to refuse
unusable declarations; and members who read facts and must never see a fact whose content violates what its capability
promised. Platform maintainers need evidence that the boundary holds end to end.

### User Story 1 - Acceptance rejects facts whose payload violates the capability's declared schema (Priority: P1)

An Analyst capability declares, for each fact kind it emits, the shape its payload must have. The declaration is
imported with the Analyst image, and every run that uses the capability keeps the declaration it was created with. When
an attempt completes, the platform checks every fact of the result against the declared shape of its kind. If every
fact conforms, the result is accepted and indexed as before. If any fact does not conform, the whole attempt ends as
failed with a named reason that identifies the fact kind and the position of the offending fact, and nothing of that
result becomes visible to members.

**Why this priority**: It is the feature's purpose: payloads stop being unchecked content, and members only ever read
facts that match their capability's promise.

**Independent Test**: Complete one attempt with conforming facts and one with a single nonconforming payload among many
conforming ones, both against a capability that declares payload schemas. The first is accepted and its facts are
readable; the second ends as failed with the named reason and the run exposes no fact of it.

**Acceptance Scenarios**:

1. **Given** a run pinned to a capability that declares a payload schema for the kind `K`, **When** an attempt
   completes with facts of kind `K` whose payloads all conform, **Then** the result is accepted and its facts are
   indexed and readable exactly as submitted.
2. **Given** the same run, **When** an attempt completes with one fact of kind `K` whose payload violates the schema
   (a missing required property, a wrong type, or an undeclared property where the schema forbids it), **Then** the
   attempt ends as failed with a named reason that identifies the kind and the offending fact, the normal retry rules
   apply, and no fact of the result is visible, not even the conforming ones.
3. **Given** a capability that declares schemas for some kinds only, **When** a result contains a fact of a kind
   without a declared schema, **Then** the result is rejected with a named reason, because a capability that declares
   payload schemas declares every kind it emits.
4. **Given** a capability whose manifest declares no payload schemas, **When** its results are accepted, **Then** the
   behavior is unchanged from today: the payload only has to be a JSON object within the size limit.
5. **Given** a run created before this feature or from a node snapshot without payload schemas, **When** its attempts
   complete, **Then** they are validated as before and are not retroactively subject to any schema.

---

### User Story 2 - An Analyst author declares payload schemas in the manifest and gets them imported, pinned, and shared (Priority: P1)

An Analyst author adds, per fact kind, a payload schema to the versioned Analyst manifest. When a Club Admin registers
the image by digest, the Registry imports the declaration with the capability, refuses a declaration whose payload
schemas are invalid or unsupported, and keeps the schemas with the cached manifest. The Registry's profile and workflow
versions that bind the capability, and each new run's node snapshot, pin the exact schemas, so a later image
registration or a new capability version never changes what an existing run validates against. The manifest contract
change is released as a new minor version of the manifest contract with golden valid and invalid examples that the
platform and the Python Analyst SDK both validate.

**Why this priority**: Without a declared, pinned, and shared contract, acceptance has nothing stable to validate
against and authors have nothing to write against.

**Independent Test**: Register an image whose manifest declares payload schemas and inspect the imported declaration;
register one with an invalid schema and observe the refusal; create a run, register a newer image of the same
capability with a changed schema, and confirm the existing run still validates against the schema it was created with.

**Acceptance Scenarios**:

1. **Given** a manifest that declares a valid payload schema for each emitted fact kind, **When** the image is
   registered by digest, **Then** the declaration is imported with its schemas and shown unchanged in the registered
   image's capability declaration.
2. **Given** a manifest with a payload schema that is not a valid schema, uses an unsupported feature, references
   external resources, or is larger than the limit, **When** the image is registered, **Then** registration is refused
   with a named reason and no declaration is cached.
3. **Given** a run created from a workflow whose node uses the capability, **When** the run's node snapshot is read,
   **Then** it contains the pinned payload schemas (or their content digest), and a later registration of a different
   declaration does not change them.
4. **Given** the released manifest contract versions, **When** the new minor version is validated, **Then** every
   earlier valid manifest is still valid, and the golden examples (valid and invalid, with and without payload
   schemas) produce the same verdicts in the platform's contract tests and in the SDK's tests.

---

### User Story 3 - The SDK validates facts before writing them (Priority: P2)

A capability author builds an Analyst with the SDK. Whenever the Analyst's own manifest declares payload schemas, the
SDK automatically checks every fact's payload against the schema of its kind before it writes the result, with no
opt-in call, and a violation always raises an error that names the fact kind and the offending fact and prevents the
result from being written. The check is the same one the platform applies at acceptance, so an Analyst that passes it
locally is not rejected for payload shape later.

**Why this priority**: It moves the failure to where the author can fix it, but the platform check in Story 1 is
what keeps members safe, so it ranks below it.

**Independent Test**: In an SDK unit test, write conforming facts successfully and attempt to write a nonconforming
one; the second raises the named error and no result is produced.

**Acceptance Scenarios**:

1. **Given** an Analyst whose manifest declares payload schemas, **When** it writes facts that conform, **Then** the
   facts are validated automatically and the result is written as before.
2. **Given** the same Analyst, **When** it writes a fact whose payload violates its kind's schema, **Then** the SDK
   fails the write with an error naming the kind and the fact, and writes no result, without the author having asked
   for the check and without any way to downgrade the failure to a warning.
3. **Given** an Analyst whose manifest declares no payload schemas, **When** it writes facts, **Then** the SDK
   behaves as before.
4. **Given** an Analyst whose manifest declares payload schemas, **When** it writes a fact of a kind that has no
   declared schema, **Then** the SDK fails the write with the same named reason as the platform's acceptance check.

---

### User Story 4 - The reference media probe proves the path end to end (Priority: P2)

A new capability version of the reference capability (for example `reference-media-probe` 1.1.0) declares a payload
schema for its `frame-second-probe` fact in its manifest, while the existing 1.0.0 version stays unchanged. Both
versions are registered and tested side by side. The Docker-backed end-to-end evidence registers the new image, runs
it on a synthetic recording, and shows that
the conforming facts are accepted and readable exactly as the Analyst wrote them, and that a deliberately
nonconforming variant of the result ends as failed with the named reason and exposes no facts.

**Why this priority**: It is the real-container evidence for the boundary; the behavior itself is already specified
by Stories 1 to 3.

**Independent Test**: Run the Docker-backed reference Analyst end-to-end tests: the run is accepted and its facts
read back equal to the written payloads; the nonconforming path ends as failed with the named reason.

**Acceptance Scenarios**:

1. **Given** the registered payload-validated reference image, **When** a run over a synthetic recording completes,
   **Then** every segment result is accepted and every fact payload read through the member fact read is identical to
   what the Analyst wrote.
2. **Given** the same registration and a result whose facts violate the pinned schema (for example a registered schema stricter than the Analyst's own), **When** the attempt completes, **Then**
   it ends as failed with the named reason and the run exposes no fact of that segment.
3. **Given** the unchanged 1.0.0 reference image registered next to the new version, **When** a run over a synthetic
   recording completes, **Then** its results are accepted as before without payload validation, and its existing
   end-to-end evidence needs no change.

---

### Edge Cases

- A payload conforms to the schema but exceeds the existing payload size limit: it is still rejected by the size
  limit, with that limit's existing named reason.
- A result contains many nonconforming facts: the attempt fails once, names the first offending fact in result order,
  and reports how many were found, without storing any detail beyond a bounded reason.
- A schema is satisfied only by values the platform stores differently (for example number precision): validation runs
  against the submitted JSON values, and the accepted payload is stored and returned exactly as submitted.
- A manifest declares a schema for a kind its capability never emits: allowed; unused schemas do not fail a result.
- Two kinds share the same schema content: allowed; each kind is pinned independently.
- A schema takes long to evaluate or recurses deeply: validation is bounded, and a result that exceeds the bound is
  rejected with a named reason instead of hanging acceptance.
- A manifest older than the new contract version is registered: it declares no payload schemas and behaves as today.
- The attempt is already fenced out, cancelled, or superseded when acceptance runs: the existing outcomes apply first;
  payload validation never turns such an attempt into an accepted result.
- Validation failure leaves no partial index, no accepted result record, and no visible fact, even when the failure
  is found after earlier facts of the same result were checked.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The versioned Analyst manifest contract MUST allow a capability to declare, per fact kind, a payload
  schema, and the change MUST be a backward-compatible minor release: every manifest valid under the previous
  version stays valid and declares no payload schemas.
- **FR-002**: The contract MUST fix the supported schema language as a restricted, self-contained subset of JSON
  Schema 2020-12: `type`, `properties`, `required`, `additionalProperties`, `items`, `enum`, `const`, numeric, string,
  and array bounds, and internal `$defs`/`$ref` only, with no external references and no format assertions. It MUST
  also fix the size and depth limits of a declared schema. A schema that is invalid, uses a feature outside the
  subset, references anything outside itself, or exceeds a limit MUST make the manifest invalid. The platform and the
  Python SDK MUST implement exactly this subset so that they give identical verdicts.
- **FR-003**: Registering an image by digest MUST import the declared payload schemas with the capability
  declaration, MUST refuse an invalid declaration with a named reason without caching it, and MUST show the declared
  schemas as part of the registered image's capability declaration.
- **FR-004**: The profile and workflow bindings of a capability and each run's node snapshot MUST pin the declared
  payload schemas as part of the capability declaration, so that neither a later registration nor a new capability
  version changes what an existing run validates against.
- **FR-005**: Result acceptance MUST validate the payload of every fact of the result against the schema pinned in
  the run's node snapshot for the fact's kind, in addition to the existing envelope, window, count, and size checks.
- **FR-006**: When a capability declares any payload schema, a fact of a kind that has no declared schema MUST fail
  acceptance with a named reason.
- **FR-007**: When the pinned declaration contains no payload schemas, acceptance MUST behave exactly as before this
  feature.
- **FR-008**: A result with any nonconforming payload MUST end its attempt as failed with a named reason that
  identifies the fact kind and the first offending fact's position, and the existing retry rules MUST apply.
- **FR-009**: A failed payload validation MUST leave no accepted result, no indexed fact, and no member-visible trace
  of the result's facts; a result's facts MUST become visible only together, in the same transaction as the
  acceptance.
- **FR-010**: Fact payloads MUST be stored and returned by every existing read exactly as accepted; validation MUST
  NOT interpret, default, coerce, or rewrite a payload.
- **FR-011**: Payload validation MUST be bounded in time and memory per result and MUST end the attempt as failed
  with a named reason when the bound is exceeded; the memory bound is the existing per-result facts size limit plus
  instance depth 32.
- **FR-012**: The new manifest contract version MUST ship with golden valid and invalid examples (with and without
  payload schemas, and covering each refusal in FR-002), a released baseline schema, and release compatibility
  checks, and the platform and the Python Analyst SDK MUST both validate the same examples with identical verdicts.
- **FR-013**: The SDK's embedded copy of the analysis contracts MUST be refreshed to the new version, and its drift
  check MUST pass.
- **FR-014**: Whenever the Analyst's own manifest declares payload schemas, the SDK MUST automatically validate every
  fact against the schema of its kind before writing the result, without an opt-in by the Analyst author; a violation
  MUST always fail the write by raising an error that names the fact kind and the offending fact, and MUST prevent any
  result from being written. A manifest without payload schemas MUST leave SDK behavior unchanged.
- **FR-015**: The SDK's payload check MUST give the same verdict as the platform's acceptance check for the same
  schema and payload, demonstrated by shared golden payload examples.
- **FR-016**: The existing reference-media-probe capability version 1.0.0 (manifest, image, and evidence) MUST stay
  unchanged. A new capability version of it (for example 1.1.0) MUST declare a payload schema for its
  `frame-second-probe` fact, rely on the SDK's automatic validation before writing, and keep its facts deterministic.
  Both versions MUST be registered and tested side by side.
- **FR-017**: Docker-backed end-to-end evidence MUST show an accepted run of the payload-validated reference image
  (the new version) whose facts read back identical to what was written, and a result whose facts violate the pinned schema (for example a registered schema stricter than the Analyst's own) that ends as
  failed with the named reason and exposes no fact; the existing 1.0.0 evidence MUST keep passing unchanged.
- **FR-018**: Acceptance metrics and audit records MUST distinguish payload validation failures from other
  acceptance failures by their named reason, and MUST NOT record payload contents beyond a bounded excerpt of the
  reason.
- **FR-019**: The architecture documentation MUST describe payload schema declaration, pinning, and validation as
  implemented development evidence, and MUST stop stating that capability-specific payload validation is future
  work, while keeping any remaining gaps explicit.

### Key Entities

- **Fact Payload Schema**: The declared definition of the payload shape for one fact kind of one capability version;
  immutable once imported; identified by its content digest.
- **Capability Declaration**: The imported, versioned declaration of an Analyst capability; now includes the payload
  schemas by fact kind.
- **Node Snapshot**: The immutable per-run record of a workflow node's resolved declaration and profile binding; now
  pins the payload schemas used to validate the node's results.
- **Payload Validation Failure**: The named reason on a failed attempt that identifies the fact kind, the offending
  fact's position, and the JSON pointer of the offending payload location.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In acceptance tests, 100% of results containing at least one payload that violates its pinned schema
  end as failed with the named reason, and 0 of their facts are visible to members.
- **SC-002**: In acceptance tests, 100% of results whose payloads all conform are accepted and read back
  byte-for-byte equal in payload content to what the Analyst wrote.
- **SC-003**: Every manifest valid before this feature remains valid, and every previously accepted result in the
  existing test suites is still accepted without changes to its fixtures.
- **SC-004**: The platform's contract tests and the SDK's tests give identical verdicts on every shared golden
  manifest and payload example.
- **SC-005**: An Analyst author can detect a nonconforming fact in a local unit test, before any container image is
  built, with an error that names the fact kind and the offending fact.
- **SC-006**: Payload validation of a result at the maximum permitted fact count adds no more than 10% to the
  acceptance time of the same result without payload schemas in the integration test environment, or the bound
  failure in FR-011 is demonstrably reached instead of a hang.
- **SC-007**: The Docker-backed reference Analyst evidence passes for both the accepted and the rejected path on a
  Linux engine.

## Assumptions

- The payload schema language is the restricted, self-contained subset of JSON Schema 2020-12 fixed by FR-002; the
  plan fixes its limits, and no external references are resolved.
- Payload schemas are part of the capability declaration and therefore change the declaration's content digest;
  profile and workflow versions that already pin a declaration are unaffected until a new declaration is bound.
- Validation applies only to results of runs whose node snapshot carries payload schemas; existing runs keep their
  behavior, and no migration rewrites existing snapshots or facts.
- The declared schemas are the author's contract; the platform does not judge whether a schema is semantically
  meaningful, only that it is valid and within limits.
- This feature adds no member endpoint and no Web UI change; the registered image's capability declaration appears
  through the existing registry reads, and facts through the existing fact reads.
- The reference capability stays development evidence, is never part of a catalog, and its payload-validated new
  version (for example 1.1.0, next to the unchanged 1.0.0) is registered only by tests and the documented local
  procedure.
- The size and depth limits, the validation time bound, and the named reason codes are development values chosen by
  the plan; production values remain unresolved.
- Quality metrics, payload interpretation, payload rewriting on reads, and any catalog capability are out of scope.
- Dependencies: the implemented specs Analysis Result Manifests and Validated Acceptance, Accepted Fact Indexing and
  Capability-Scoped Fact Queries, Analyst Registry, Python Analyst SDK, and Reference Analyst Container on the Python
  SDK. It implements the Result Manifest And Lineage and Capability Declaration sections of
  `docs/architecture/analysts-models-and-hardware.md` and the catalog rules of
  `docs/architecture/analyst-capability-catalog.md`.

# Recording Lineage Specification

## Purpose

Defines authorized source-recording upload, immutable recording and timeline lineage, and finalized evidence sets that downstream analysis can reference without reinterpretation.

## ADDED Requirements

### Requirement: Recording operations use current Match and Team authorization

The platform SHALL resolve every upload and finalization request through the target Match's authoritative Team scope. It SHALL permit the operation only for an authenticated active member who is either a current Club Admin or has the current Coach role for that Team, and SHALL fail closed when the Match, Team scope, membership, capability, or role cannot be resolved.

#### Scenario: Club Admin operates on a Match

- **WHEN** an active member with current Club Admin authority requests a recording operation for an existing Match
- **THEN** the request proceeds to application validation for that Match and its resolved Team

#### Scenario: Coach operates within the granted Team

- **WHEN** an active member with the current Coach role for Team A requests a recording operation for a Match owned by Team A
- **THEN** the request proceeds to application validation

#### Scenario: Coach targets another Team

- **WHEN** a member whose Coach role is for Team A requests a recording operation for a Match owned by Team B
- **THEN** the request is forbidden
- **THEN** no upload, lineage, recording-set, idempotency-success, or outbox state is created or changed

#### Scenario: Match scope cannot be resolved

- **WHEN** a recording operation identifies a missing Match or its authoritative Team scope cannot be resolved
- **THEN** the request fails closed with the applicable not-found or forbidden outcome
- **THEN** no recording state is changed

### Requirement: Upload grants are narrow and keep video bytes off the control plane

An authorized upload-start operation SHALL create a Match-scoped upload intent without accepting video bytes. Grant issuance SHALL return a time-bounded S3-compatible upload grant restricted to that intent's one server-selected object key and permitted upload operation, and SHALL NOT grant list, read, delete, overwrite of another key, or access to another Match or Team. The control-plane API SHALL NOT proxy the recording bytes.

#### Scenario: Authorized upload is started

- **WHEN** an authorized actor starts an upload with valid Match-scoped metadata
- **THEN** the platform returns an upload-intent identity associated with the resolved Match and Team
- **THEN** no completed recording version or timeline mapping exists yet

#### Scenario: A grant is issued

- **WHEN** the authorized actor requests an upload grant for a pending upload intent
- **THEN** the returned grant permits upload only to that intent's server-selected object key for a bounded lifetime
- **THEN** the grant conveys no authority to list objects or read, delete, or overwrite any other object

#### Scenario: A grant is requested for an unusable intent

- **WHEN** grant issuance targets an unknown, differently scoped, completed, cancelled, or otherwise non-pending upload intent
- **THEN** the request is rejected
- **THEN** no broader or replacement storage authority is issued

### Requirement: Upload completion verifies storage evidence before accepting immutable lineage

Upload completion SHALL verify that the expected object exists at the upload intent's assigned key and that its immutable storage evidence agrees with the completion request. A successful completion SHALL create a new immutable recording-version identity and an immutable timeline-mapping identity bound to that recording version, Match, Team, object identity, algorithm-qualified media digest, and mapping digest. A mismatch or storage-verification failure SHALL create neither accepted lineage nor a successful completion outcome.

#### Scenario: Uploaded object and completion evidence agree

- **WHEN** an authorized actor completes a pending upload whose expected object, size, media digest, and submitted mapping evidence agree
- **THEN** one completed recording version and one timeline mapping are accepted for the upload intent
- **THEN** their immutable identities and digests are returned

#### Scenario: Uploaded object evidence disagrees

- **WHEN** completion evidence does not match the object at the intent's assigned key
- **THEN** completion is rejected
- **THEN** no recording version or timeline mapping is accepted

#### Scenario: Timeline mapping targets another recording

- **WHEN** a completion or lineage command associates a timeline mapping with a recording version other than the one from which that mapping was derived
- **THEN** the command is rejected
- **THEN** neither lineage identity is changed

### Requirement: Recording versions and timeline mappings are immutable revisions

An accepted recording version and timeline mapping SHALL remain unchanged and addressable by identity. Any correction to recording metadata, object evidence, or mapping inputs SHALL create new immutable identities; it SHALL NOT update, replace, or reinterpret an identity already accepted or referenced by a finalized recording set.

#### Scenario: Recording evidence is corrected

- **WHEN** an authorized actor supplies corrected recording metadata or object evidence after a recording version was accepted
- **THEN** the correction is represented by a new recording-version identity and corresponding lineage
- **THEN** the prior recording version remains unchanged

#### Scenario: Timeline mapping is corrected

- **WHEN** corrected mapping inputs differ from an accepted timeline mapping
- **THEN** the correction receives a new timeline-mapping identity and digest
- **THEN** the prior mapping and every finalized set that references it remain unchanged

### Requirement: Finalization freezes one ordered Match-scoped evidence set

An authorized finalization SHALL create a new immutable recording-set-version identity for exactly one Match and its resolved Team. The request SHALL contain at least one ordered membership, and every membership SHALL reference one completed recording version and its exact timeline mapping from the same Match and Team scope. The persisted ordinal order and referenced identities SHALL be the frozen evidence visible to downstream lineage queries.

#### Scenario: A valid recording set is finalized

- **WHEN** an authorized actor finalizes a nonempty ordered list of valid recording-version and timeline-mapping pairs for one Match
- **THEN** one immutable recording-set version is created with the supplied membership order
- **THEN** later reads return the same ordered identities and digests

#### Scenario: An empty set is submitted

- **WHEN** an authorized actor attempts to finalize a recording set with no memberships
- **THEN** the request is rejected as invalid
- **THEN** no recording-set version or finalized event is created

#### Scenario: A membership crosses scope

- **WHEN** any requested membership belongs to another Match or Team
- **THEN** the complete finalization request is rejected
- **THEN** no partial set, membership, or finalized event is created

#### Scenario: A mapping does not belong to its recording version

- **WHEN** a membership pairs a recording version with a timeline mapping accepted for another recording version
- **THEN** the complete finalization request is rejected
- **THEN** no recording-set version or finalized event is created

#### Scenario: Finalized evidence is corrected

- **WHEN** an authorized actor corrects membership, order, recording version, or timeline mapping after finalization
- **THEN** a new recording-set-version identity is created by a new successful finalization
- **THEN** the prior finalized set and its membership order remain unchanged and addressable

### Requirement: Mutation retries have scoped idempotent outcomes

Upload start, upload completion, and recording-set finalization SHALL accept an opaque nonempty idempotency key scoped to the operation and target Match. The platform SHALL bind the key to a canonical request fingerprint and durable outcome. Replaying the same scoped key and fingerprint SHALL return the original durable outcome without repeating the mutation; reusing it with a different fingerprint SHALL return a conflict. Idempotency records SHALL NOT contain presigned URLs, object credentials, session secrets, or video bytes.

#### Scenario: A successful request is replayed

- **WHEN** a caller repeats a successful mutation with the same operation, Match scope, idempotency key, and canonical request fingerprint
- **THEN** the platform returns the original durable resource identity and outcome
- **THEN** no duplicate recording version, mapping, recording set, membership, or finalized event is created

#### Scenario: A key is reused for different content

- **WHEN** a caller reuses an idempotency key in the same operation and Match scope with a different canonical request fingerprint
- **THEN** the request is rejected as an idempotency conflict
- **THEN** the original outcome remains unchanged

#### Scenario: The same key is used in a different scope

- **WHEN** the same opaque key is used for a different operation or Match
- **THEN** it is evaluated independently within that distinct scope
- **THEN** it conveys no authority to the other Match

### Requirement: Finalization and its canonical event are atomic

A successful recording-set finalization SHALL commit the recording-set version, all ordered memberships, its successful idempotency outcome, and exactly one canonical recording-finalized event atomically. The event SHALL identify the event contract version, event identity, recording-set-version identity, Match, Team, and frozen ordered lineage without containing a presigned URL, storage credential, or video bytes. Any failure before commit SHALL leave none of those records committed.

#### Scenario: Finalization commits

- **WHEN** a valid finalization transaction succeeds
- **THEN** the finalized set, every ordered membership, the successful idempotency outcome, and exactly one canonical finalized event are committed together
- **THEN** the event describes the same frozen lineage returned by the finalization outcome

#### Scenario: Finalization rolls back

- **WHEN** persistence fails after finalization processing begins but before commit
- **THEN** no recording-set version, membership, successful idempotency outcome, or finalized event from that request remains committed

#### Scenario: Finalization is replayed

- **WHEN** a committed finalization is replayed with the same scoped idempotency key and request fingerprint
- **THEN** the original recording-set outcome is returned
- **THEN** the canonical event count for that recording-set version remains exactly one

### Requirement: Recording operations and lineage are represented through typed contracts

The versioned OpenAPI document SHALL describe authenticated JSON operations for upload start, narrowly scoped upload-grant issuance, upload completion, and recording-set finalization, including applicable validation, unauthenticated, forbidden, not-found, idempotency-conflict, and storage-dependency outcomes. Downstream platform components SHALL be able to query a finalized set's immutable Match, Team, ordered recording-version, timeline-mapping, and digest lineage through a typed Recordings boundary without direct access to Recordings storage.

#### Scenario: The OpenAPI document is inspected

- **WHEN** a contributor retrieves the versioned OpenAPI document
- **THEN** every supported recording operation and its authentication and principal response outcomes are present
- **THEN** no segment-materialization, analysis-scheduling, production-storage-administration, or client-UI operation is introduced

#### Scenario: Finalized lineage is queried

- **WHEN** an authorized downstream platform component queries an existing recording-set version through the Recordings lineage boundary
- **THEN** it receives the immutable Match, Team, ordered membership identities, and digests needed to validate evidence
- **THEN** it requires no direct Recordings-schema or object-store access

### Requirement: Real PostgreSQL and S3-compatible boundaries provide acceptance evidence

The implementation SHALL verify persistence and object-storage behavior against disposable PostgreSQL and S3-compatible services rather than substitutes that bypass their transaction, constraint, presigning, object-metadata, or authorization semantics.

#### Scenario: Recording integration evidence is executed

- **WHEN** the Recordings integration suite runs with a supported container runtime
- **THEN** it covers authorized upload and finalization, authorization denial, immutable recording and mapping revisions, mapping mismatch, empty and cross-scope sets, idempotent replay and conflict, finalization rollback, and exactly one canonical finalized event
- **THEN** it verifies uploaded-object existence and evidence through an S3-compatible API and transactional state through PostgreSQL

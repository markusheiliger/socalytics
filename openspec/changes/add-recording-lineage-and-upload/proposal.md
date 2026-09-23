# Add Recording Lineage and Upload Proposal

## Why

The platform has no executable Recordings behavior, so it cannot accept source recordings or freeze auditable evidence for later analysis. This change establishes the first Recordings capability around immutable upload lineage, authorized finalization, and atomic event evidence while the surrounding analysis and media-processing workflows remain deferred.

## What Changes

- Add immutable recording versions and immutable timeline mappings owned by the Recordings module; corrections create new identities instead of mutating accepted lineage.
- Add finalized recording-set versions with ordered memberships that bind exact recording-version and timeline-mapping identities for one Match and Team scope.
- Add scoped idempotency outcomes for upload start, upload completion, and recording-set finalization so an identical replay returns the recorded outcome while conflicting reuse is rejected.
- Add a Recordings-owned transactional outbox that commits exactly one canonical recording-finalized event with successful finalization and commits neither state nor event when the transaction fails.
- Expose authenticated, OpenAPI-visible operations to start an upload, issue a narrowly scoped presigned S3-compatible upload grant, complete the upload, and finalize a Match recording set.
- Authorize these operations only for a current Club Admin or a current member with the target Team's Coach role, resolving Match and Team scope through typed Club and IdentityAccess boundaries rather than direct schema access.
- Add PostgreSQL and S3 Testcontainers evidence for successful workflows, authorization failures, immutable revisions, mapping mismatch, empty and cross-scope sets, idempotent replay and conflict, atomic rollback, and one canonical finalized event.
- Depend on the active `add-platform-persistence-foundation` change for shared database composition, migrations, module-scoped Dapper access, explicit transactions, schema isolation, and PostgreSQL Testcontainers infrastructure.
- Depend on the active `add-club-identity-foundation` change for typed Match-to-Team scope resolution, authenticated sessions, current membership, Club Admin authority, and a typed Team-role authorization boundary extended to represent Coach authority. This change does not modify either active change's artifacts or access their schemas directly.
- Keep segment materialization, analysis scheduling or run creation, outbox transport publication, production object-storage selection and credentials, lifecycle-policy values, and client UI out of scope.
- Preserve production storage topology, presigned-grant lifetime, upload-size and media-format policy, retention/deletion values, encryption/key custody, audit authority, and event-transport operations as unresolved production decisions.

## Capabilities

### New Capabilities

- `recording-lineage`: Defines authorized S3-compatible upload orchestration, immutable recording and mapping lineage, finalized ordered recording sets, scoped idempotency, and atomic canonical finalized-event evidence.

### Modified Capabilities

None.

## Impact

- Affected product areas: `SocAlytics.Platform.Recordings`, API composition and OpenAPI, Recordings-owned PostgreSQL migrations and Dapper handlers, S3-compatible storage integration, host tests, Recordings PostgreSQL/S3 integration tests, and architecture tests.
- Active-change dependency: persistence foundation behavior must be applied first or available as equivalent accepted evidence. Club and IdentityAccess typed boundaries must also be available before the Recordings authorization workflow is implemented; Recordings tables, SQL, idempotency outcomes, outbox records, and storage-key policy remain owned by this change.
- API impact: new versioned JSON operations for upload start, upload completion, and recording-set finalization, including authenticated, forbidden, validation, not-found, conflict, and idempotent-replay outcomes. Video bytes continue to flow directly between the authorized client and S3-compatible storage rather than through the control plane.
- Dependency impact: S3-compatible client support and an S3 Testcontainers fixture are added using centrally managed versions; PostgreSQL, Dapper, DbUp, and PostgreSQL Testcontainers support come from the persistence foundation and must not be duplicated.
- Documentation impact: apply synchronizes `docs/architecture/match-data-pipeline.md`, `docs/architecture/platform-implementation.md`, `docs/architecture/security-and-data-governance.md`, repository/platform development guidance, and component-local executable contract guidance justified by implementation.
- Security and governance impact: supplies development evidence for team-scoped access and portions of BND-001, DAT-003, CTL-009, THR-003, and POL-003 without claiming production approval. Presigned URLs and object credentials must not enter durable idempotency records, outbox payloads, audit records, logs, or telemetry.
- UX, segment materialization, analysis scheduling, client applications, and production deployment configuration are unaffected.

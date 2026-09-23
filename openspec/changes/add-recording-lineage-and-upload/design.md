# Recording Lineage and Upload Design

## Context

See `proposal.md` for motivation and `specs/recording-lineage/spec.md` for required behavior. The executable .NET 10 host currently composes an empty Recordings module through one public registration method; it has no persistence, object storage, authentication, domain endpoints, or Recordings tests. The active `add-platform-persistence-foundation` and `add-club-identity-foundation` changes plan the infrastructure and typed authorization behavior this change consumes, but neither has completed implementation tasks.

The adopted architecture places the `recordings` PostgreSQL schema, upload metadata, immutable lineage, recording-set versions, and module outbox in Recordings. Video bytes use an S3-compatible data plane and never cross the API. Club owns Match-to-Team scope, IdentityAccess owns current membership and authority, and capability projects remain peers without direct schema access or project references.

Impact categories:

- **UX:** No client is added. The API exposes workflow states and structured outcomes suitable for future clients; redirects, upload progress UI, retry UI, and accessibility behavior are unaffected.
- **Architecture:** Recordings gains its first domain model, S3-compatible data-plane adapter, typed lineage query, endpoint mapping, module-owned migrations, and transactional outbox. Typed host adapters compose Club and IdentityAccess without capability-to-capability references.
- **Security and governance:** Presigned grants are short-lived, one-key write authority and are never durable. DAT-003 metadata and lineage become development evidence; production encryption, retention, deletion, residency, credentials, audit authority, and storage configuration remain unresolved and blocking for production.
- **Implementation:** Adds centrally managed S3 client and S3 Testcontainers dependencies, a Recordings integration-test project, internal Dapper handlers and SQL, API composition, and focused host and architecture coverage. It reuses persistence packages and fixtures from the active persistence foundation.
- **Documentation:** Apply synchronizes `docs/architecture/match-data-pipeline.md`, `docs/architecture/platform-implementation.md`, `docs/architecture/security-and-data-governance.md`, root/platform development guidance, and a component-local Recordings executable contract where justified.

## Goals / Non-Goals

**Goals:**

- Make accepted recording, mapping, and finalized-set lineage append-only and database-enforced.
- Keep video transfer in S3-compatible storage while the API owns authorization, intent, completion verification, and immutable metadata.
- Re-evaluate current Match scope and Team authority at every operation without direct cross-module SQL or capability references.
- Make mutation retries deterministic and make finalization state plus its one canonical event atomic under concurrency and failure.
- Expose the smallest typed Recordings lineage query needed by future analysis validation without implementing analysis behavior.

**Non-Goals:**

- Segment creation, media transcoding or inspection, analysis run creation, job scheduling, NATS publication, outbox cleanup, or consumer behavior.
- Client screens, resumable or multipart-upload UX, download grants, storage browsing, deletion, retention, legal holds, or lifecycle propagation.
- Selecting a production S3 provider, bucket topology, credentials, grant duration, object-size limit, accepted media formats, encryption mechanism, or checksum-provider policy.
- Changing Club or IdentityAccess persistence. If the active identity change does not expose typed current Club Admin and Team Coach-equivalent authority, apply remains blocked until equivalent accepted behavior is available.

## Decisions

### 1. Gate implementation on both active foundations

Apply begins only after `add-platform-persistence-foundation` supplies equivalent accepted connection, migration, transaction, schema-isolation, concurrency, and PostgreSQL-test behavior and `add-club-identity-foundation` supplies authenticated sessions plus typed current Match scope and Team authority behavior. Recordings adds only its own tables, SQL, storage adapter, handlers, contracts, and tests; it does not duplicate shared persistence or alter the active changes' artifacts.

The IdentityAccess boundary must answer a typed recording-management authorization question using current membership and either Club Admin authority or the Team's Coach-equivalent operation grant. The external behavior remains the named Coach role required by the recording spec; unrestricted role strings and stale cookie claims are not authoritative.

Alternative considered: query `club` and `identity_access` tables from Recordings. Rejected because it violates ownership, bypasses current authorization behavior, and defeats extraction and schema-isolation tests.

### 2. Compose typed Club and IdentityAccess calls through host adapters

Recordings declares narrow public application ports for resolving an operation's Match/Team authority and keeps handlers dependent only on those ports. The API host, which already references all capability modules, registers adapters that call the public typed Club Match-scope query and IdentityAccess current-authority query. Recordings handlers invoke the composed port immediately before state mutation, so endpoint metadata is not the enforcement boundary.

This dependency inversion preserves peer capability project references: Recordings never imports Club or IdentityAccess types, and the host adapter never accesses their stores or schemas. Denial returns a bounded typed result and correlation context, not foreign entities. Architecture tests constrain the added public types to registration, endpoint mapping, required ports, and immutable lineage query contracts.

Alternative considered: introduce a shared domain-contract project. Rejected for this slice because host composition already owns cross-module policy orchestration and a shared project would establish a broader dependency surface without demonstrated reuse. The adapter refinement follows the active Club/Identity design and is not an ADR candidate unless apply requires a new shared contracts assembly or changes the peer-capability rule.

### 3. Separate mutable upload intent from immutable accepted lineage

The `recordings` schema adds upload intents, recording versions, timeline mappings, recording-set versions, ordered memberships, idempotency outcomes, and outbox records. An upload intent is workflow state with a server-generated opaque identity, Match and Team snapshot, unguessable object key, pending/completed state, and concurrency version. Completion is its only successful terminal transition.

A completion transaction inserts a recording version and its initial timeline mapping with new opaque identities. Recording-version rows contain Match and Team scope, object key and provider-neutral object identity, byte length, algorithm-qualified media digest, immutable metadata, and creation evidence. Mapping rows contain the recording-version identity, canonical mapping representation, algorithm-qualified mapping digest, and creation evidence. Database privileges expose no update or delete path for accepted lineage; foreign keys and unique constraints prevent remapping an identity to another recording.

The first mapping representation is a versioned, canonical component-local value that records media-time to match-time coverage and from which a stable SHA-256 digest is computed. Its exact wire schema and canonical serialization belong in the Recordings executable contract during apply; adding segment-policy or materialization fields is prohibited. Any changed canonical input creates a new mapping identity and digest.

Alternative considered: create a recording-version row at upload start and update it on completion. Rejected because a workflow transition would blur pending state with immutable accepted evidence and complicate correction semantics.

### 4. Generate one-key upload grants from pending intents

Recordings owns a provider-neutral object-store port with operations to presign one upload and inspect one object. Its initial adapter uses a maintained S3-compatible .NET client selected and centrally versioned during apply. The bucket/container reference comes from configuration; the module generates an opaque key under a stamp-local Recordings prefix and never accepts a caller-selected key.

Grant issuance first re-authorizes the Match and verifies the intent is pending and in scope. It returns a bounded write grant for exactly that key and permitted method with configured content constraints supported by the selected S3-compatible implementation. It grants no list, read, delete, or alternate-key authority. The URL and signing material exist only in the response and process memory; logs, traces, idempotency records, database rows, audits, and outbox payloads exclude them.

The lifetime, maximum size, accepted media types, checksum enforcement profile, credentials, bucket topology, encryption, and production endpoint remain configuration/policy inputs, not approved defaults. Tests provide explicit ephemeral values and a disposable S3-compatible service.

Alternative considered: stream uploads through ASP.NET Core. Rejected because it violates the adopted S3 data plane, expands sensitive-byte handling, and adds avoidable API capacity pressure.

### 5. Verify object evidence before the completion transaction

Completion re-authorizes the Match, loads the pending intent, and inspects only its assigned object. It compares key, byte length, provider object identity, and an algorithm-qualified SHA-256 media digest supplied and preserved through the supported S3 checksum/metadata mechanism. Provider ETags are never treated as media digests. A missing object, unsupported verification evidence, or mismatch returns a storage/validation failure before accepted lineage is written.

After verification, one explicit PostgreSQL transaction claims the pending intent using its current version, inserts the immutable recording version and mapping, and records the successful idempotency outcome. The unique intent completion constraint and optimistic transition prevent concurrent completions. Storage is external to the database, so an uploaded but unaccepted object may remain after failure; lifecycle cleanup is deferred and the object never becomes accepted lineage without a committed completion.

Alternative considered: trust client metadata without inspecting S3. Rejected because it could accept a missing or different object. A distributed transaction with object storage is also rejected because S3-compatible storage does not participate in the PostgreSQL transaction.

### 6. Finalize ordered exact lineage in one transaction

A finalization command carries one Match identity plus a nonempty ordered list of `(recording-version-id, timeline-mapping-id)` pairs. After current authorization, Recordings loads every pair in one module-scoped transaction and verifies completed status, mapping ownership, Match and Team equality, and unambiguous positive ordinals. Any missing, duplicate, mismatched, or cross-scope member rejects the complete request.

Success inserts one recording-set-version row and all membership rows. Unique constraints cover set identity, `(set, ordinal)`, and `(set, recording-version, mapping)` as appropriate. There is no update or delete application path. A correction invokes finalization again with a new idempotency key and creates a new set identity; old sets coexist and the typed lineage query always returns their original order and digests.

Alternative considered: maintain one mutable current set per Match. Rejected because mutation would reinterpret evidence already used by analysis and destroy auditability.

### 7. Persist scoped idempotency outcomes with canonical request hashes

Each mutating command requires an opaque key. The durable uniqueness scope is `(operation-kind, match-id, idempotency-key)`, and the record stores a versioned SHA-256 hash over the command's canonical, secret-free semantic inputs plus a compact typed success outcome. Actor identity is evidence but not part of key scope, allowing a safely retried request to recover its outcome after session renewal while current authorization is still re-evaluated before disclosure.

Handlers reserve or resolve the key in the same transaction as their mutation. An equal hash returns the original resource identities; a different hash conflicts. Concurrent equal requests converge through the uniqueness constraint and transaction retry/read path. Validation, authorization, and external-storage failures do not create successful idempotency outcomes and may be retried after correction. Grant issuance is not a mutation replay surface: a fresh authorized request may issue a fresh short-lived URL for the same pending intent, and no URL is reconstructed from durable state.

Alternative considered: scope keys globally or by actor. Global scope creates unrelated collisions; actor scope prevents legitimate recovery after authority/session changes and does not replace current authorization.

### 8. Commit one canonical finalized event in the Recordings outbox

The finalization transaction serializes one versioned canonical event from the same validated ordered values used for membership inserts. The Recordings-owned outbox stores event identity, contract name/version, recording-set identity, Match and Team identities, ordered immutable lineage, occurrence time, correlation and actor evidence, payload digest, and unpublished state. It contains no URL, credential, object bytes, or mutable storage metadata.

A unique constraint on `(event-contract, recording-set-version-id)` and deterministic handling of the idempotency record guarantee one event per finalized set under replay and concurrency. Set, memberships, success outcome, and outbox row commit or roll back together. This change does not add a publisher, NATS resource, retry loop, delivery status transition, or cleanup policy; those require a later messaging change.

Alternative considered: publish directly after commit. Rejected because a process failure can lose the event or duplicate an untracked publication. A shared outbox table is rejected because the architecture assigns outgoing state to the owning module.

### 9. Expose narrow endpoints and a read-only lineage boundary

Recordings adds public registration and endpoint-mapping surfaces used by the API host. Versioned JSON operations cover upload start, grant issuance, completion, and set finalization. They use the active BFF authentication and CSRF behavior, structured problem details, explicit idempotency keys on mutations, and OpenAPI metadata for success and failure outcomes. Domain handlers, not endpoint declarations, enforce authorization and invariants.

The typed finalized-lineage query returns only immutable set, Match, Team, ordered recording-version, mapping, digest, and object-reference identities needed for future validation. It exposes no SQL, Dapper types, upload grant, mutable intent, or object-store client. Analysis scheduling remains absent.

Alternative considered: expose Recordings entities or repository interfaces. Rejected because persistence representation is not an application contract and would permit callers to bypass lineage validation.

### 10. Separate architecture, host, and real-infrastructure evidence

Add `Tests/SocAlytics.Platform.Recordings.Tests` using the persistence foundation's PostgreSQL fixture plus a disposable S3-compatible container. Tests exercise real migrations, constraints, transactions, object inspection, and presigned upload behavior. They cover Admin and matching Coach success; unauthenticated, missing, revoked, and cross-Team denial; immutable recording/mapping correction; object and mapping mismatch; empty/cross-scope finalization; idempotent replay/conflict/concurrency; injected rollback; and exactly one canonical event.

Host tests exercise the complete authenticated HTTP workflow with ephemeral PostgreSQL/S3 configuration and inspect OpenAPI and secret-free diagnostics. Architecture tests enforce schema and SQL ownership, peer capability references, bounded exports, no direct Club/IdentityAccess persistence access, no video body handling, and absence of presigned values from durable/audit/telemetry models.

Tests require a supported container runtime and never fall back to developer or production services. Local S3 composition outside tests is added only if needed to make the AppHost the supported executable workflow; it remains development configuration and does not select production storage.

## Risks / Trade-offs

- **[The active foundations may land without the required typed Coach authority]** -> Treat equivalent typed current-authority behavior as an apply gate; do not bypass it with schema reads, role strings, or cookie claims.
- **[Presigned PUT constraints vary across S3-compatible implementations]** -> Select and test one supported local implementation, keep the port provider-neutral, expose only guarantees proven by integration tests, and leave production-provider conformance blocked.
- **[Object verification and database commit cannot be one distributed transaction]** -> Verify before commit, use an unguessable immutable key, prevent client delete authority, accept unreferenced failed uploads as deferred lifecycle work, and never expose them as accepted lineage.
- **[Canonical hash or digest drift could break replay and lineage]** -> Version canonicalization, hash semantic typed values rather than transport JSON, store algorithm-qualified digests, and add stable-vector tests.
- **[A module outbox without a publisher accumulates rows]** -> Keep the initial volume bounded to finalizations, expose no delivery claim, and require a later messaging/lifecycle change before production use.
- **[Host adapters expand public module surfaces]** -> Keep contracts narrow and immutable and enforce exact exported types and dependency direction in architecture tests.
- **[Development audit evidence is not the independent audit authority]** -> Emit only minimized records through the available typed audit boundary and preserve GOV-AUD/POL-009 as production blocking.

## Migration Plan

1. Confirm both active foundation changes are implemented or available as equivalent accepted behavior; stop rather than duplicate missing contracts.
2. Add centrally managed S3/Testcontainers dependencies and the Recordings test project, then add additive Recordings migrations and constraint tests.
3. Implement object-store, typed authorization, idempotency, upload-intent, completion, immutable-lineage, finalization, and outbox behavior with focused PostgreSQL/S3 tests after each slice.
4. Add endpoint/OpenAPI mapping and host adapters, then extend authenticated host tests and architecture tests.
5. Add the typed finalized-lineage query, synchronize architecture and development documentation, resolve the conditional ADR candidate, and run restore, build, all tests, OpenSpec validation, independent verification, and security/governance audit.

Application rollback removes endpoint, adapter, and object-store wiring while leaving additive immutable Recordings tables and uploaded development objects intact. Applied migrations are not edited or automatically reversed; corrections use forward migrations, while disposable developer/test stores may be explicitly reset. Rollback never mutates or reassigns a previously finalized identity.

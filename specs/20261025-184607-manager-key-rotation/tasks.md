---

description: "Task list for Analyst Manager Device Key Rotation"
---

# Tasks: Analyst Manager Device Key Rotation

**Input**: Design documents from `/specs/20261025-184607-manager-key-rotation/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/key-rotation.md, quickstart.md

**Tests**: Included. The plan's Testing context and Principle IV (evidence over claims) require integration, contract, Manager, and Docker-backed end-to-end evidence.

**Organization**: Tasks are grouped by user story. Consecutive unchecked `[P]` tasks under one heading run in parallel and touch different files.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change different files and depend on no task of their group
- **[Story]**: User story the task belongs to (US1 to US4)

## Path Conventions

- Platform: `src/platform/SocAlytics.Platform.<Layer>/Registry/...`; tests in `src/platform/Tests/`
- Manager: `src/analysts/manager/SocAlytics.Analysts.Manager.Core/...`; tests in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/`
- Contract: `contracts/analyst-manager/key-rotation/v1/`

## Phase 1: Setup (Contract Foundation)

**Purpose**: The versioned contract and golden fixtures shared by the platform and the Manager (FR-019).

### Contract files

- [ ] T001 [P] Create the JSON Schema 2020-12 `contracts/analyst-manager/key-rotation/v1/key-rotation.schema.json` for the request (`replacementPublicJwk` EC P-256 public JWK without private members, `claimedKeyProtection`, `oldKeyProof`, `replacementKeyProof`), the `200` outcome (`switched` | `already-switched`, `managerId`, `oldKeyThumbprint`, `newKeyThumbprint`, `rotationCount`, `switchedAt`), and the problem codes of `contracts/key-rotation.md`, following `contracts/analyst-manager/registration/v1/registration.schema.json`
- [ ] T002 [P] Create `contracts/analyst-manager/key-rotation/v1/fixtures.json` (vectors with `exchange`, `file`, `seedState`, `expected`: success, same-proofs retry, and one vector per refusal code), the valid request, response, and problem examples, an `invalid/` folder of negative examples, and a second fixed replacement test key `replacement-key.jwk.json` beside the registration contract's test key; no key material beyond public test keys, assertions, or tokens (SC-007)

### Contract tests

- [ ] T003 Add `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/KeyRotationContractTests.cs` validating the examples, the negative examples, and the fixture vectors (modelled on `AnalystManagerRegistrationFixtureTests.cs`) and register the contract in the contract catalog and index checks (`ContractCatalog.cs`, `ContractIndexTests.cs`), including the key-rotation schema row in the contracts index in `contracts/README.md` that `ContractIndexTests` requires

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Persistence, domain rule, options, audit vocabulary, and store abstractions every story needs.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

- [ ] T004 Add migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/0033_registry_analyst_manager_key_rotation.sql` (renumber to the next free number on an up-to-date default branch): `key_rotation_count integer NOT NULL DEFAULT 0` on `socalytics.analyst_manager_registration`; table `socalytics.analyst_manager_key_rotation` with `id uuid` PK, `registration_id uuid NOT NULL` FK, `sequence integer NOT NULL` `UNIQUE (registration_id, sequence)`, `old_thumbprint text NOT NULL` indexed, `new_thumbprint text NOT NULL` `UNIQUE (registration_id, new_thumbprint)`, `old_proof_jti_hash bytea NOT NULL`, `new_proof_jti_hash bytea NOT NULL`, `claimed_key_protection text NOT NULL`, `correlation_id text NOT NULL`, `switched_at timestamptz NOT NULL`; no `version`; extend the proof replay kind check with `key_rotation_old` and `key_rotation_new`
- [ ] T005 Add `analyst_manager_key_rotation` to `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` as an immutable table without `version`
- [ ] T006 Add `AnalystManagerRegistration.RotateKey(newJwk, newThumbprint, now)` and `KeyRotationCount` in `src/platform/SocAlytics.Platform.Domain/Registry/AnalystManagerRegistration.cs`: requires `State == Active` and `newThumbprint != KeyThumbprint`, otherwise `invalid-state-transition` or `rotation-not-a-replacement`; `PublicJwk` and `KeyThumbprint` change only through it; identity, stamp, label, state, and history are unchanged

### Application and domain vocabulary

- [ ] T007 [P] Add `KeyRotation` options (`ProofLifetime` default 2 minutes, `RetryWindow` default 2 minutes, development values; validation requires `RetryWindow` <= `ProofLifetime`) and `RateLimits.KeyRotationPerMinute` (development default 5) with validation to `src/platform/SocAlytics.Platform.Application/Registry/RegistryOptions.cs`, and the development defaults to the API host configuration `src/platform/SocAlytics.Platform.Api/appsettings.Development.json`
- [ ] T008 [P] Add `analyst-manager.key-rotation-requested`, `-succeeded`, `-refused` and the detail keys `oldKeyThumbprint`, `newKeyThumbprint`, `rotationCount` to the allow-list in `src/platform/SocAlytics.Platform.Application/Registry/RegistryAuditEvents.cs`, with the stable reason vocabulary of the audit-reason table in `data-model.md` and `contracts/key-rotation.md`
- [ ] T009 [P] Add `SwitchKeyAsync` (updates only the public key, thumbprint, `key_rotation_count` and `version`, guarded by `version` and the expected old thumbprint) to `src/platform/SocAlytics.Platform.Application/Registry/Abstractions/IRegistrationStore.cs`
- [ ] T010 [P] Create `src/platform/SocAlytics.Platform.Application/Registry/Abstractions/IKeyRotationStore.cs` (insert a rotation row, lookups of retired thumbprints of a registration, of another registration's current or retired thumbprint, and of the latest rotation row for the retry window) and `src/platform/SocAlytics.Platform.Application/Registry/KeyRotation/KeyRotationOutcomes.cs` (outcome and stable refusal codes of the contract)
- [ ] T011 [P] Extend `src/platform/SocAlytics.Platform.Application/Registry/Abstractions/IManagerProofValidator.cs` and `src/platform/SocAlytics.Platform.Infrastructure/Registry/JoseManagerProofValidator.cs` with an expected-claims check (audience = rotation URI, `sub` = Manager ID, binding claim `new_jkt` or `old_jkt`, `jti`, `iat`, `exp` judged by platform time) reusing the `ES256` client-assertion validation

### Infrastructure stores

- [ ] T012 [P] Implement `SwitchKeyAsync` in `src/platform/SocAlytics.Platform.Infrastructure/Registry/DapperRegistrationStore.cs` (one UPDATE of `public_jwk`, `key_thumbprint`, `key_rotation_count` guarded by `version` and the expected old thumbprint; zero rows means stale)
- [ ] T013 [P] Create `src/platform/SocAlytics.Platform.Infrastructure/Registry/DapperKeyRotationStore.cs` implementing `IKeyRotationStore`
- [ ] T014 [P] Support the replay kinds `key_rotation_old` and `key_rotation_new` (SHA-256 of the `jti`, with the proof's expiry) in `src/platform/SocAlytics.Platform.Infrastructure/Registry/DapperProofReplayStore.cs`
- [ ] T015 Register the options and stores (the handler is registered by T018) in the Application and Infrastructure composition methods (`src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`, `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`) as internal implementation types

### Foundational tests

- [ ] T016 [P] Add domain tests for `RotateKey` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/Domain/AnalystManagerRegistrationKeyRotationTests.cs` (Active only, not-a-replacement, identity unchanged)
- [ ] T017 [P] Add store tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationStoreTests.cs` (atomic switch with rotation row, stale on a lost race, retired and other-registration lookups, uniqueness constraints, replay kinds)

---

## Phase 3: User Story 1 - Rotate the device key of an active Manager (Priority: P1) 🎯 MVP

**Goal**: An Active Manager replaces its device key through one operation and one local-control command, keeping its identity.

**Independent Test**: Register and activate a Manager, run `rotate-key`, and confirm an unchanged identity, a new fingerprint in the fleet read, working token and queue credentials under the new key, and refusal of everything bound to the old key.

### Handler and Manager building blocks

- [ ] T018 [P] [US1] Implement the `RotateManagerKey` handler in `src/platform/SocAlytics.Platform.Application/Registry/KeyRotation/RotateManagerKey.cs` and register it in `ApplicationServiceCollectionExtensions.cs`: validate both bound proofs (FR-002, FR-003), record the `jti` hashes, refuse not-a-replacement, previously retired (`key-rotation-key-previously-retired`), and other-registration keys (`key-rotation-replacement-unavailable`, no detail), unsupported key types, software keys under `SoftwareKeyPolicy` (FR-005), non-Active registrations (FR-009); do not call `ManagerVersionGate` (FR-021); switch in one `IUnitOfWork` with `SwitchKeyAsync`, insert the rotation row, and `IQueueCredentialStore.MarkRevokedAsync` so `IQueueAccessRevoker` pushes revocations after commit (FR-006 to FR-008)
- [ ] T019 [P] [US1] Add the rotation request, response, and problem types to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformContracts.cs` and the rotation call (old-key token and DPoP) to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs`
- [ ] T020 [P] [US1] Add the bound old-key and replacement-key proof builders (`new_jkt`, `old_jkt`, audience = rotation URI, `jti`, `iat`, `exp`) to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Platform/ProofFactory.cs`
- [ ] T021 [P] [US1] Add the optional `pendingRotation` (`replacementKeyReference`, `replacementFingerprint`, `startedAt`) with atomic write and removal to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalState/ProtectedRegistrationState.cs`, signed with the key registered at the time of writing (FR-016)
- [ ] T022 [P] [US1] Add the `rotate-key` request and response to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalControl/LocalControlMessages.cs` and `src/analysts/manager/contracts/local-control.schema.json`

### Endpoint and rotation coordinator

- [ ] T023 [P] [US1] Add the request and response contracts to `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationContracts.cs` and the `POST /api/v1/analyst-managers/self/key-rotation` endpoint `rotateAnalystManagerKey` (tag `Analyst Manager self`, policy `AuthorizationPolicyNames.AnalystManager`, `RequireHttpsEndpointFilter`, `JsonOnlyFilter`, `Cache-Control: no-store`, problem responses with the stable codes, nothing about other registrations) in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs`, and in the same task refresh the committed OpenAPI snapshot in a development checkout (`SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`, never in the verification environment, as `AGENTS.md` requires), add `rotateAnalystManagerKey` to `src/platform/Tests/SocAlytics.Platform.Host.Tests/ContractOperations.cs` and `OpenApiContractTests.cs`, and refresh the Kiota client output in `src/clients/web` (snapshot and generated client only, no Web UI change)
- [ ] T024 [P] [US1] Implement `KeyRotationCoordinator` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/KeyRotationCoordinator.cs`: single-instance rotation lock, refusal without an Active registration, replacement key from the same `IDeviceKeyProvider` as a non-exportable key with no fallback (FR-014), pending entry before the request, both proofs, request, then on success atomic new file signed with the new key, drop the pending entry, delete the old key, clear the token cache, and request fresh queue credentials

### Rate limit and Manager surface

- [ ] T025 [P] [US1] Add the policy `registry-key-rotation` partitioned by the registration ID claim with `KeyRotationPerMinute` and the `analyst-manager-rate-limited` problem code (`429` with `Retry-After`) to `src/platform/SocAlytics.Platform.Api/Registry/RegistryRateLimiting.cs` and apply it to the endpoint in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs` (FR-023)
- [ ] T026 [P] [US1] Serve `rotate-key` in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/LocalControl/LocalControlServer.cs` and wire the coordinator in the Manager hosting composition (`src/analysts/manager/SocAlytics.Analysts.Manager.Core/Hosting/`, tray and server profiles)
- [ ] T027 [P] [US1] Add the `socalytics-manager rotate-key` command to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Cli/ManagerCli.cs`, reporting identity and old and new fingerprints, and an actionable "no Manager running" message with no change when nothing answers local control (FR-015)

### Tests for User Story 1

- [ ] T028 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationTests.cs` covering success with identity unchanged, old token, DPoP, and queue credentials refused with no grace period (SC-002), new token under the new key, every refusal code of the contract leaving the key unchanged (SC-004), the software-key policy, non-Active states, a Manager whose reported version is below `Registry:ManagerVersion:Minimum` rotating successfully while its queue-credential and claim operations remain refused by the version gate (FR-021), `429` with `Retry-After`, and a proof issued by a skewed Manager clock refused (expiry judged by platform time) with the actionable `key-rotation-old-proof-invalid` or `key-rotation-replacement-proof-invalid` code
- [ ] T029 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationConcurrencyTests.cs` proving concurrent rotations of one Manager switch at most once (SC-005) and that held attempts continue under the new key (FR-021)
- [ ] T030 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationFleetReadTests.cs` proving the fleet read shows the new fingerprint with the status history intact and no new screen (FR-020, SC-006)
- [ ] T031 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/KeyRotationCoordinatorTests.cs` (SoftHSM2, scripted platform): success path, refusal without Active registration, refusal when the provider cannot create a non-exportable key, second concurrent rotation refused, and a paused or draining Manager rotates and keeps its intent unchanged
- [ ] T032 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/GoldenFixtures/KeyRotationFixtureTests.cs` consuming `contracts/analyst-manager/key-rotation/v1/` fixtures
- [ ] T033 [P] [US1] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalControl/KeyRotationLocalControlTests.cs` for the `rotate-key` command, its output, and "no Manager running"

**Checkpoint**: A Manager rotates its key end to end on the happy path.

---

## Phase 4: User Story 2 - Recover safely when rotation is interrupted (Priority: P1)

**Goal**: Every interruption converges on one key without re-registration.

**Independent Test**: Interrupt rotation after key creation, during transport, after the platform switch, and during the file update; restart; the Manager and platform end on one key with the identity kept.

### Authentication branch and Manager recovery

- [ ] T034 [P] [US2] Add the rotation-only retired-key retry branch to `src/platform/SocAlytics.Platform.Application/Registry/MachineAccess/AuthenticateManagerRequest.cs`: a token bound to a retired thumbprint whose rotation row is within `Registry:KeyRotation:RetryWindow` and whose DPoP proof validates with that retired key yields a principal marked `RetiredKeyRetry`, evaluated only for the rotation request path
- [ ] T035 [P] [US2] Add transport retry within the window with the same proofs to `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/KeyRotationCoordinator.cs`, and startup recovery from a pending entry: token with the replacement key means switched (complete the local update, delete the old key, clear the token cache), a refusal not following a valid key proof means not switched (token with the old key, delete the replacement key, drop the pending entry), never clearing the protected registration (FR-017, FR-018)

### Handler retry outcome and restore wiring

- [ ] T036 [P] [US2] Make `src/platform/SocAlytics.Platform.Application/Registry/KeyRotation/RotateManagerKey.cs` return `already-switched` for a `RetiredKeyRetry` principal whose two proof `jti` hashes equal the recorded rotation's (before recording replays, no second switch; within `RetryWindow` only the recorded `jti` hashes and the rotation row are compared, with no expiry re-check of the proofs), and refuse `key-rotation-retired-key` (`401`) otherwise (FR-011)
- [ ] T037 [P] [US2] Run the pending-rotation recovery before normal registration restore in `src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/RegistrationRestorer.cs`, and keep the existing inactive-state interpretation only for a refusal following a valid proof of the registered key

### Tests for User Story 2

- [ ] T038 [P] [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationRetryTests.cs`: same-proofs retry with the old key within the window returns `already-switched` with one switch; different proofs, a retry beyond the window, and other endpoints with the old key are refused; replacement-key authentication works after the switch
- [ ] T039 [P] [US2] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/Registration/KeyRotationRecoveryTests.cs`: interruption after replacement key creation, after the pending entry, during transport, after the switch before the response, and during the file replace converge on one key without re-registration (SC-003); a corrupt file fails closed; an unreachable platform changes nothing and clears no registration
- [ ] T040 [P] [US2] Add `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests/LocalState/PendingRotationStateTests.cs` for the atomic write, removal, and signing key of the pending entry

**Checkpoint**: Rotation is safe to run after a suspected exposure.

---

## Phase 5: User Story 3 - Audit and observe rotations (Priority: P2)

**Goal**: Every requested, succeeded, and refused rotation is audited with fingerprints only.

**Independent Test**: Perform successful, refused, replayed, throttled, and outer-authentication-failed rotations and read the audit events.

- [ ] T041 [US3] Write the `requested`, `succeeded`, and `refused` events (Manager, stamp, old and new fingerprints, `claimedKeyProtection`, `rotationCount`, correlation, stable reason) from `src/platform/SocAlytics.Platform.Application/Registry/KeyRotation/RotateManagerKey.cs` using the allow-list only (FR-012); the `requested` event and every `refused` event are written in their own committed unit of work, independent of the switch transaction, and `succeeded` is written in the switch unit of work
- [ ] T042 [US3] Audit outer machine-authentication refusals on the rotation path that identify a registration (token hash lookup or thumbprint lookup) in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerDPoPAuthenticationHandler.cs`, and audit each throttled request through an `OnRejected` branch reading the registration claim in `src/platform/SocAlytics.Platform.Api/Registry/RegistryRateLimiting.cs`; requests tied to no registration produce only ordinary logging; the reasons are those of the audit-reason table in `data-model.md` and `contracts/key-rotation.md`

### Tests for User Story 3

- [ ] T043 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationAuditTests.cs` for one audit event per outcome and reason (including rate limited and outer authentication failure with and without an identifiable registration) and for a stale or rolled-back switch still leaving the `requested` and `refused` events
- [ ] T044 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationSecretLeakTests.cs` proving no audit event, log, response, or fixture contains key material, assertions, or tokens (SC-007), extending the pattern of `AuditAndSecretTests.cs`

**Checkpoint**: Rotations are observable without exposing secrets.

---

## Phase 6: User Story 4 - A lost key is not recoverable by an administrator (Priority: P3)

**Goal**: No administrator, member, escrow, or rebind path to set or replace a Manager's key.

**Independent Test**: Member sessions of every role are refused on the rotation operation and no member operation can change a key.

- [ ] T045 [P] [US4] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/KeyRotationAuthorizationTests.cs`: member sessions of every role and unauthenticated callers are refused on `rotateAnalystManagerKey`, and a rotation without a valid old-key proof is refused (FR-010, SC-008)
- [ ] T046 [P] [US4] Add an architecture test in `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs` that the rotation endpoint requires the `AnalystManager` policy and that `rotateAnalystManagerKey` is the only endpoint that replaces a key and the only production caller of `IRegistrationStore.SwitchKeyAsync` is the `RotateManagerKey` handler (an architecture dependency rule); T045 remains the behavioral check

**Checkpoint**: The lost-key guarantee is verified by absence.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [ ] T047 Re-check the committed OpenAPI snapshot, `ContractOperations.cs`, `OpenApiContractTests.cs`, and the Kiota client output in `src/clients/web` against the final API (the refresh itself is done in T023), refreshing again in a development checkout only if they differ (snapshot and generated client only, no Web UI change)
- [ ] T048 [P] Add the Docker-backed end-to-end test `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/KeyRotationEndToEndTests.cs`: a real Manager rotates against the stamp, keeps its identity, works under the new key, the old key is refused, and the `rotate-key` command completes within 60 seconds (SC-001)
- [ ] T049 Make `docs/architecture/` true for the merged state: re-read `docs/architecture/analyst-manager.md` (Machine Authentication paragraph into present tense as development evidence, Registration And Stamp Binding, local control commands), `platform-implementation.md` (Registry area, API surface, rate limiting, audit events, persistence), `analyst-runtime-and-recovery.md`, `security-and-data-governance.md` (the `GOV-CRED-002` row and `GOV-CRED-003`), and `contracts-and-compatibility.md`, and replace every planned, future-tense, or unimplemented wording about Manager key rotation with development-evidence wording while keeping production values, key custody, attestation, scheduled or automatic rotation, and administrator rotation explicitly unresolved or out of scope
- [ ] T050 Update the evidence enumerations with the rotation capability in `AGENTS.md`, `README.md`, `src/platform/README.md`, `src/analysts/manager/README.md`, and the evidence enumeration of `contracts/README.md` (its schema index row is added by T003)
- [ ] T051 Run the quickstart validation (`specs/20261025-184607-manager-key-rotation/quickstart.md` steps 1 to 7, where steps 6 and 7 only verify their results) and `node .github/scripts/check-markdown.mjs`, and fix findings

---

## Dependencies & Execution Order

- Phase 1 has no dependencies; T003 follows T001 and T002.
- Phase 2 follows Phase 1 (T004 and T006 first, then the grouped tasks, T015 after them, tests last); it blocks all stories.
- US1 (Phase 3) follows Phase 2. The handler (T018) runs beside the Manager building blocks (T019 to T022); the endpoint (T023) and the coordinator (T024) follow and run together; the rate limit and the Manager surface (T025 to T027) follow them.
- US2 (Phase 4) follows US1 (extends `RotateManagerKey.cs`, `KeyRotationCoordinator.cs`).
- US3 (Phase 5) follows US1 (extends `RotateManagerKey.cs`, `RegistryRateLimiting.cs`); it may run beside US2 only after T036, which also edits `RotateManagerKey.cs`.
- US4 (Phase 6) needs the operation from US1 and can run beside US2 and US3.
- Polish follows all stories.

## Parallel Example

- Foundational vocabulary: T007 to T011 together, then T012 to T014 together.
- User Story 1: T018 to T022 together, then T023 with T024, then T025 to T027 together, and T028 to T033 tests together.
- User Story 2: T034 with T035, then T036 with T037, then T038 to T040 together.

## Implementation Strategy

- **MVP**: Phases 1 and 2 plus User Story 1 (the happy path), then User Story 2, because rotation that can strand a Manager must not ship without recovery.
- Then US3 (audit), US4 (guarantee by absence), and Polish.

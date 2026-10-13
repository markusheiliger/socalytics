---

description: "Task list for Analyst Registry: Manifests, Profiles, and Workflow Resolution"
---

# Tasks: Analyst Registry: Manifests, Profiles, and Workflow Resolution

**Input**: Design documents from `specs/20261012-152148-analyst-registry/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/analyst-manifest-and-packaging.md](contracts/analyst-manifest-and-packaging.md), [contracts/registry-api.md](contracts/registry-api.md), [quickstart.md](quickstart.md)

**Tests**: Required by the constitution. Every task adds the tests for the code it creates in the same task, so the platform solution builds (warnings are errors) and its test suites stay green after each task.

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Platform Persistence Foundation (unit of work, migrations, `socalytics.attach_version_trigger`, `PersistedTableClassifications.cs`), Club and Identity Foundation (`MapMemberApi`, `SessionAntiforgeryFilter`, `IfMatchHeader`, `ProblemResults`, `IAccessAuthorizer`, `ClubPermission.Administer`, `IAuditTrail`), Recording Lineage and Upload (`contracts/README.md` index, `SocAlytics.Platform.Contracts.Tests` with `ContractCatalog`), and Durable Analysis Workflow (`IWorkflowDefinitionResolver`, `ResolvedWorkflowDefinition`, `ConfiguredWorkflowDefinitionResolver`, `ConfiguredWorkflowOptions`, `WorkflowDefinitionAvailability`, run creation and snapshots). The migration takes the next free number on an up-to-date default branch (`0015` at planning time).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The manifest contract, Registry storage, the OCI reader, and the domain rules every story needs.

### Contract and domain rules

- [x] T001 [P] Analyst manifest contract (contracts/analyst-manifest-and-packaging.md § Analyst manifest; FR-003): add `contracts/analysis/analyst-manifest/v1/analyst-manifest.schema.json` (JSON Schema 2020-12 with the required properties of the contract and `additionalProperties: false`), `contracts/analysis/analyst-manifest/v1/releases/1.0.0.schema.json`, valid examples under `examples/valid/` (segment-scoped low-level, match-scoped high-level with upstream capabilities) and invalid examples under `examples/invalid/` (missing capability, unknown property, bad digest, unknown tier); add the index row to `contracts/README.md`; tests in `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AnalystManifestContractTests.cs`.
- [ ] T002 [P] Domain rules (research.md § R4, § R6; FR-007, FR-008, FR-010): add `src/platform/SocAlytics.Platform.Domain/Registry/AnalystProfile.cs` (states and transitions), `Domain/Registry/CapabilityDeclaration.cs`, `Domain/Registry/ProfileValidation.cs` (profile against declaration: capability, image and model digests, schema versions, requirement minimums), `Domain/Registry/WorkflowValidation.cs` (approved profiles only, required upstream capabilities and schema versions, conditional edges, cycles, requested capabilities, completion and segmentation policies), and `Domain/Registry/CanonicalJson.cs` (RFC 8785) with `ContentDigest`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/Domain/` including the RFC 8785 examples.

### Storage and image reader

- [ ] T003 Registry storage (data-model.md; research.md § R5; uses the domain types from T002): add `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_registry_analyst_registry.sql` (next free number) creating `registry.analyst_capability_declaration`, `registry.analyst_profile`, `registry.workflow_definition`, and `registry.default_workflow` with the columns, keys, and immutability triggers of data-model.md and `socalytics.attach_version_trigger` on `analyst_profile` and `default_workflow`; classify the two tables without `version` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`; add `ICapabilityDeclarationStore`, `IAnalystProfileStore`, and `IWorkflowDefinitionStore` in `src/platform/SocAlytics.Platform.Application/Registry/` and their internal Dapper implementations in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Registry/`; register them in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/RegistryStorageTests.cs`: round trips, triggers refuse updates and deletes of declarations and workflow definitions, content changes of non-draft profiles, and invalid state transitions.
- [ ] T004 OCI image reader and test registry (research.md § R1, § R2, § R3, § R9; contracts/analyst-manifest-and-packaging.md § Import refusals; FR-001, FR-002, FR-005): add `IOciImageReader` in `src/platform/SocAlytics.Platform.Application/Registry/Images/IOciImageReader.cs`; add internal `src/platform/SocAlytics.Platform.Infrastructure/Registry/Oci/ImageRegistryOptions.cs` (`Registry:Images`: `RegistryHost`, `RepositoryPrefix`, optional `Username` and `Token`; HTTPS required outside Development and Test), `Registry/Oci/OciDistributionClient.cs` (bearer challenge, manifest by digest with OCI and Docker media types, index to `linux/amd64`, blob fetch, SHA-256 verification), and `Registry/Oci/OciImageReader.cs` (configuration labels and the last layer only; tar extraction of `opt/socalytics/analyst-manifest.json`; refusal codes of the contract); register them in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs` (also changed by T003, so this task runs after it). Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/Support/OciRegistryContainerFixture.cs` (`registry:2`) and `Registry/Support/TestImagePublisher.cs` (filler layer, manifest layer last, configuration with labels, push through the Distribution API). Tests in `Registry/OciImageReaderTests.cs`: reads only manifest, configuration, and last layer (counting handler, SC-004), and each refusal.

**Checkpoint**: The contract, storage, domain rules, and the OCI reader exist and are tested.

---

## Phase 2: User Story 1 - Register an Analyst image and cache its declaration (Priority: P1) 🎯 MVP

**Goal**: Club Admins register images by digest; declarations are cached immutably.

**Independent Test**: Publish a test image, register it by digest, read the declaration; register invalid variants and confirm refusals with nothing stored.

- [ ] T005 [US1] Image registration (contracts/registry-api.md rows 1–3; FR-001–FR-005, FR-015, FR-016): add `RegisterAnalystImageCommand` in `src/platform/SocAlytics.Platform.Application/Registry/Images/RegisterAnalystImage.cs` (digest-only and allowed-prefix references, reads through `IOciImageReader`, validates through `IAnalystManifestValidator`, checks labels against the manifest, stores idempotently, records `registry.analyst-image.registered` or `…refused`), `IAnalystManifestValidator` in `Application/Registry/Images/`, its internal implementation with `JsonSchema.Net` against the released schema in `src/platform/SocAlytics.Platform.Infrastructure/Registry/AnalystManifestValidator.cs`, the new audit event types in `src/platform/SocAlytics.Platform.Application/Registry/RegistryAuditEvents.cs`, and `POST`/`GET /api/v1/registry/analyst-images` and `GET /api/v1/registry/analyst-images/{digest}` in `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs` (Club Admin; refusal codes per the contract). Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AnalystImageImportTests.cs` covering US1 scenarios 1–7 and the edge cases on digests, labels, and duplicate capabilities.

**Checkpoint**: User Story 1 works and is the MVP.

---

## Phase 3: User Story 2 - Create, approve, and retire Analyst profiles (Priority: P2)

**Goal**: Content-addressed profiles with draft, approved, and retired states.

**Independent Test**: Create, approve, attempt to change, version, and retire profiles; check digests, states, and audit events.

- [ ] T006 [US2] Profile operations (contracts/registry-api.md profile rows; clarification on self-approval; FR-006–FR-009, FR-015, FR-016): add `CreateAnalystProfile`, `ApproveAnalystProfile`, and `RetireAnalystProfile` in `src/platform/SocAlytics.Platform.Application/Registry/Profiles/` (content validated with `ProfileValidation`, digest from `ContentDigest`, idempotent creation for identical content, approval with `If-Match` and the `selfApproval` flag in the audit event, retirement refused while the default workflow uses the profile) and the profile endpoints in `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AnalystProfileTests.cs` covering US2 scenarios 1–5 and the concurrent-approval edge case.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 4: User Story 3 - Define workflows and resolve runs from the Registry (Priority: P3)

**Goal**: Workflow definitions from approved profiles, one default, and run creation resolving from the Registry.

**Independent Test**: Define a two-node workflow, make it the default, finalize a recording set, and compare the run snapshot with the Registry; change the default and confirm the earlier run is unchanged.

- [ ] T007 [US3] Workflow definitions and the default (contracts/registry-api.md workflow rows; FR-010, FR-011, FR-015, FR-016): add `DefineWorkflow` and `SetDefaultWorkflow` in `src/platform/SocAlytics.Platform.Application/Registry/Workflows/` (validation with `WorkflowValidation`, definition digest, atomic default change with `If-Match`, audit events) and the workflow and default-workflow endpoints in `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs`. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/WorkflowDefinitionTests.cs` covering US3 scenarios 1–3 and the validation refusals.
- [ ] T008 [US3] Registry-backed resolution and the development seed (research.md § R7; FR-012, FR-013, FR-014; SC-001, SC-006): add internal `src/platform/SocAlytics.Platform.Infrastructure/Registry/RegistryWorkflowDefinitionResolver.cs` (one read transaction; `NotConfigured` without a default; `InvalidDefinition` when a referenced profile is retired or missing; output identical in shape to the configured resolver) and `Registry/ConfiguredWorkflowSeeder.cs` (hosted, Development and Test only, seeds an empty Registry from `ConfiguredWorkflowOptions` as `source = seed` declarations, approved profiles, and a default workflow; each seed declaration is a schema-valid manifest synthesized from the configured node (capability, tier, scope, digests, timing values; upstream from the configured edges; `videoAccess` `optional`; determinism `true`) and validated with `IAnalystManifestValidator`; the seeder records one `registry.seeded` audit event with the system as actor); register the new resolver instead of `ConfiguredWorkflowDefinitionResolver` in `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`; adjust the resolver wrappers in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/CompletionTests.cs` and `Analysis/WorkflowAvailabilityTests.cs` so the existing analysis tests keep passing through the seeded Registry. Tests in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/RegistryResolutionTests.cs` covering US3 scenarios 4–7 and the concurrent-default edge case, with byte-identical snapshots of existing runs after default and profile changes, and checking that the seed passes manifest validation and that the `registry.seeded` event exists.

**Checkpoint**: All user stories work.

---

## Phase 5: Polish & Cross-Cutting Concerns

- [ ] T009 Documentation: record the packaging convention (manifest file in the last layer, the `io.socalytics.*` labels, supported layer media types) in `docs/architecture/analysts-models-and-hardware.md` § Image Metadata And Packaging; update `src/platform/README.md` (Registry API, `Registry:Images` options, the Development and Test seed, the migration) and the "Current State" in `AGENTS.md`.
- [ ] T010 Run the validation in [quickstart.md](quickstart.md) and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Phase 1**: T001 and T002 in parallel; T003 after them (its stores use the domain types from T002); T004 after T003 (both change `InfrastructureServiceCollectionExtensions.cs`).
- **US1 (Phase 2)**: after Phase 1. **US2 (Phase 3)**: after US1 (profiles reference registered declarations). **US3 (Phase 4)**: after US2; T007 → T008.
- **Polish**: after all stories.

### Parallel opportunities

- Phase 1: T001 (`contracts/`, Contracts.Tests) and T002 (domain and its tests) touch disjoint files and run in parallel.

## Implementation Strategy

1. Phase 1, then User Story 1 as the MVP (images can be registered and read).
2. User Story 2 (profiles), then User Story 3 (workflows and resolution, which switches run creation to the Registry).
3. Documentation and validation.

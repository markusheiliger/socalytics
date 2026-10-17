---

description: "Task list for Web UI Analyst Registry Administration"
---

# Tasks: Web UI Analyst Registry Administration

**Input**: Design documents from `specs/20261017-185438-web-ui-registry-admin/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-023 asks for automated tests of the adapter mapping, every refusal explanation, the confirmations and their cancellation, the "has changed" handling, role-based access, accessibility rule checks, the online-only rule, and a browser test against the running platform; test work is part of the tasks below.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All paths are relative to the repository root; Web UI paths start with `src/clients/web/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never overlap. Parallel tasks never change `src/clients/web/src/i18n/en/registry.json`, `src/clients/web/src/router.tsx`, or `src/clients/web/tests/e2e/simulated/registry.spec.ts`; texts a parallel view needs beyond T001 are added by the next task without `[P]` in its phase.
- **[Story]**: Which user story this task belongs to (US1–US5).
- Every task names the files it creates or changes.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The translation namespace and client models every view and adapter uses.

- [x] T001 [P] Create the `registry` translation namespace `src/clients/web/src/i18n/en/registry.json` with English texts for every heading, label, action, confirmation, and message of `specs/20261017-185438-web-ui-registry-admin/contracts/routes-and-roles.md`: overview (default workflow, "No default workflow is set; analysis runs cannot be created until one is"), lists and filters, every field of `data-model.md` (declaration, manifest-derived fields, profile content, workflow definition, segmentation policy), display names for known tiers, scopes, profile states, edge kinds, predicate kinds, video access values, and the completion policy `all-requested-capabilities`, digest actions (show full, copy, copied, copy failed), member names ("Development seed", "Unknown member"), one message per image refusal code (`reference-not-digest`, `reference-not-allowed`, `image-not-found`, `registry-unavailable` with "try again later", `digest-mismatch`, `manifest-missing`, `manifest-invalid`, `labels-mismatch`, `unsupported-media-type`), per profile refusal (`profile-contradicts-declaration`, `image-not-registered`, `profile-content-exists`, `profile-not-draft`, `profile-in-default-workflow`, `profile-already-retired`), per workflow violation (`profile-not-found`, `profile-not-approved`, `missing-upstream`, `schema-incompatible`, `cycle`, `invalid-graph`, `requested-capability-missing`, `unknown-completion-policy`, `invalid-segmentation-policy`, `workflow-version-exists`), `workflow-not-resolvable`, a generic "refused with code {{code}}" fallback, the "has changed" message, "already registered, nothing changed", "already existed, no new version was created", the self-approval note, and the three confirmation texts; register the namespace in `ns` and `resources` of `src/clients/web/src/i18n/index.ts` (FR-012, FR-013, FR-014, FR-015, FR-016, FR-017, FR-021, FR-022)
- [x] T002 [P] Create the application-owned models of `specs/20261017-185438-web-ui-registry-admin/data-model.md` in `src/clients/web/src/platform/models/registry.ts`: `ImageSummary`, `CapabilityDeclaration` (manifest-derived fields optional), `ProfileVersion` with `rowVersion: number | undefined` and `ProfileContent`, `WorkflowSummary` with `isDefault`, `WorkflowVersion`, `WorkflowEdge` with optional `predicate`, `DefaultWorkflow` with `rowVersion`, the drafts `ImageRegistrationDraft`, `ProfileDraft`, `WorkflowDraft` (nodes with a `flagged` marker), `MemberRef` (`kind: 'member' | 'seed' | 'unknown'`), `RegistryRefusal` (`operation`, `code`, `details: { field, code }[]`, `retryable`), and the error classes `RegistryRefusalError` and `RegistryChangedError`; every enumerated value is a union of its known values and `string` ("unknown values are kept and shown raw") (FR-022)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: OpenAPI schemas for the Registry operations, response metadata, the Registry adapter, shared helpers, and the simulated API.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### Registry OpenAPI contracts

- [x] T003 Create `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryContracts.cs` with the internal request and response records of `specs/20261017-185438-web-ui-registry-admin/contracts/registry-openapi.md` (replacing the body records at the top of `AnalystRegistryEndpoints.cs` with the same property names), and change `src/platform/SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs` to serialize those records instead of anonymous objects (keeping `manifest` as the stored JSON object and mapping `definition` to its typed record) and to declare `.Accepts<T>("application/json")`, `.Produces<T>()` (with `201` where the operation creates), and the `.ProducesProblem(...)` statuses listed in the contract on all 13 operations, without changing any path, operation id, status, header, problem code, or authorization; then refresh the committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`, check that only the Registry operations and their new component schemas changed, and run `npm run build` in `src/clients/web` so the regenerated (ignored) Kiota client contains the Registry models (FR-019; research.md § R1–R2)

### Schema evidence and response metadata

- [x] T004 [P] Extend `src/platform/Tests/SocAlytics.Platform.Host.Tests/AnalystRegistryOpenApiTests.cs` with a test that, for every Registry operation id, the running API's OpenAPI document declares an `application/json` response schema for each success status and, for `registerAnalystImage`, `createAnalystProfile`, `defineWorkflow`, and `setDefaultWorkflow`, an `application/json` request schema (FR-019; contracts/registry-openapi.md § Evidence)
- [x] T005 [P] Extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AnalystProfileTests.cs` and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/WorkflowDefinitionTests.cs` with assertions that the JSON property names and nesting of the profile (`profileId`, `version`, `profileDigest`, `imageDigest`, `state`, `content.*`, lifecycle fields), workflow (`workflowId`, `workflowVersion`, `definitionDigest`, `definition.*`, `createdAt`, `createdBy`), list (`items`), and default workflow (`workflowId`, `workflowVersion`, `changedAt`, `changedBy`) responses are unchanged, and that `ETag` and `Location` headers and `201`/`200` statuses stay as before (FR-019)
- [x] T006 [P] Extend `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/AnalystImageImportTests.cs` with assertions that the image registration, list, and read responses keep their JSON property names and nesting (`imageDigest`, `repository`, `manifestDigest`, `capability.{id,version,tier,executionScope}`, `modelDigest`, `manifest` as the stored object, `source`, `registeredAt`, `registeredBy`; list `items[].{imageDigest,capabilityId,capabilityVersion,registeredAt}`), and that refusals keep their status, `code`, and `errors[]` (FR-019)
- [x] T007 [P] Create `src/clients/web/src/platform/middleware/responseMetadata.ts` with a `ResponseMetadataOption` request option and a `ResponseMetadataHandler` middleware that, when the option is present, records the response status and the `ETag` header in it; register the handler in `src/clients/web/src/platform/client.ts` before the default middlewares; add `sendWithMetadataFor(api, info, factory)` returning `{ body, status, etag }` to `src/clients/web/src/platform/adapters/kiota.ts` (errors still through `toPlatformFailure`); add `src/clients/web/tests/unit/platform/responseMetadata.test.ts` covering status and `ETag` capture, requests without the option, and error responses (research.md § R3)

### Adapter, helpers, and simulated platform

- [x] T008 [P] Create `src/clients/web/src/platform/adapters/registryAdapter.ts` implementing the `RegistryAdapter` interface of `specs/20261017-185438-web-ui-registry-admin/contracts/registry-adapter.md` over the generated client: mapping DTOs to `src/clients/web/src/platform/models/registry.ts` (missing identity, digest, or state → `PlatformFailure('incompatible')`), a tolerant parse of the stored manifest against manifest `1.0.0` (missing or mistyped fields → `undefined`, `manifestText` pretty-printed), `rowVersion` from `ETag` `"<n>"` via `sendWithMetadataFor`, `created` from `201` versus `200`, `getDefault` `404` → `null`, `setDefault` without `If-Match` only when no `rowVersion` is given, and the refusal translation of research.md § R4 (`412` and `428` on transitions and the default → `RegistryChangedError`; Registry codes → `RegistryRefusalError` with details and `retryable` only for `registry-unavailable`; unknown codes kept raw) without changing `src/clients/web/src/platform/failure.ts`; add `src/clients/web/tests/unit/platform/adapters/registryAdapter.test.ts` covering every mapping, every refusal row, `created`, `rowVersion`, `404` default, shared failures (`429`, and `503` or other server errors without a Registry refusal code, stay `PlatformFailure`s and change nothing), and incompatible responses (FR-002, FR-012, FR-013, FR-015, FR-016, FR-017, FR-019, FR-022)
- [x] T009 [P] Create `src/clients/web/src/features/registry/digest.ts` (`shortDigest(value)`: algorithm plus the first 12 hexadecimal characters; unknown forms returned unchanged) and `src/clients/web/src/features/registry/DigestText.tsx` (shortened value as text, "Show full digest" disclosure, copy button with copied and copy-failed announcements, full value selectable as text), with `src/clients/web/tests/unit/features/registry/digest.test.tsx` (FR-010, FR-021)
- [x] T010 [P] Create `src/clients/web/src/features/registry/edgeText.ts` (`describeEdge(edge, nodes)` and `describePredicate(predicate)` producing translation keys and values for "upstream → dependent", the kind, `capabilityRequested` with its capability, and `nodeAccepted` with its node, falling back to raw kinds) and `src/clients/web/src/features/registry/refusalText.ts` (`refusalMessage(refusal)` mapping every code of T001 to its key, the field of `nodes.<key>` to the node, and unknown codes to the fallback with the raw code), with `src/clients/web/tests/unit/features/registry/edgeText.test.ts` and `src/clients/web/tests/unit/features/registry/refusalText.test.ts` (FR-009, FR-012, FR-013, FR-015, FR-022)
- [ ] T011 [P] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with seedable Registry state and routes for all 13 Registry operations of `contracts/registry-adapter.md`: declarations with stored manifests, profile versions with row versions sent as `ETag`, workflow versions, and the default (`404` when none), `201`/`200` for new and existing registrations and profiles, state transitions with `409` refusals, `412` on a stale `If-Match`, `428` for a default change without `If-Match` when a default exists, scripted image refusals for every code with `errors[]`, `422` contradictions and workflow violations, `409 workflow-version-exists`, and `403` for members without the Club Admin role (FR-023)

### Query hooks and member names

- [ ] T012 Create `src/clients/web/src/features/registry/registryKeys.ts` with the query keys and TanStack Query hooks for every adapter read (no persistence, no polling) and the mutations that invalidate the affected lists, record, and default after a change, and `src/clients/web/src/features/registry/memberNames.ts` resolving member ids to `MemberRef` through `createMemberAdapter(platform).list()` (empty id → `seed`, unknown id → `unknown` with the id); add `src/clients/web/tests/unit/features/registry/memberNames.test.ts`, and the shared unit-test fixtures `src/clients/web/tests/unit/features/registry/fixtures.ts` (sample declarations with manifests, profile versions in each state, workflow versions with conditional edges, a default, and a fake `RegistryAdapter`) that the view tests of later phases use without changing; add to `src/clients/web/src/i18n/en/registry.json` any key that T009 and T010 reference but T001 did not create (research.md § R6, R8)

**Checkpoint**: The OpenAPI snapshot carries the Registry schemas, the adapter and helpers exist with passing unit tests, and the simulated API serves the Registry.

---

## Phase 3: User Story 1 - See the stamp's analysis setup (Priority: P1) 🎯 MVP

**Goal**: A Club Admin opens the Registry area and reads the default workflow, the image, profile, and workflow lists, and each record's details.

**Independent Test**: With the seeded Registry and one additional image, a Club Admin opens the area, sees the default workflow and the three lists, opens one image, one profile version, and one workflow version with every listed field; a Coach sees no navigation entry and the not-permitted view.

### Read views

- [ ] T013 [P] [US1] Create `src/clients/web/src/features/registry/RegistryOverviewPage.tsx` showing the current default workflow (id, version, changed at, changed by via `memberNames`, link to its definition) or the "no default" message, and links to the images, profiles, and workflows lists, with `src/clients/web/tests/unit/features/registry/registryOverviewPage.test.tsx` (default, no default, error with reload) (US1 acceptance 2, FR-003)
- [ ] T014 [P] [US1] Create `src/clients/web/src/features/registry/ImagesPage.tsx` (images with capability id and version, `DigestText`, registration time, a capability id filter kept in the `capabilityId` search parameter, an empty state) and `src/clients/web/src/features/registry/ImagePage.tsx` (every field of FR-005 including upstream capabilities, video access, output schema version, compatible profiles, runtime, resources, requirements, determinism, registered at and by, "not declared" for missing manifest fields, raw values for unknown ones, and the full manifest as text on request), with `src/clients/web/tests/unit/features/registry/imagesPage.test.tsx` and `src/clients/web/tests/unit/features/registry/imagePage.test.tsx` (US1 acceptance 3–4, 7, FR-004, FR-005, FR-010, FR-021, FR-022)
- [ ] T015 [P] [US1] Create `src/clients/web/src/features/registry/ProfilesPage.tsx` (versions grouped by profile id with version, state, capability id and version, shortened image digest; `state` and `capabilityId` filters in search parameters; empty state) and `src/clients/web/src/features/registry/ProfileVersionPage.tsx` (every field of FR-007 with lifecycle times and members, read through `getProfile` so `rowVersion` is known; not-found for a non-integer version without a request), with `src/clients/web/tests/unit/features/registry/profilesPage.test.tsx` and `src/clients/web/tests/unit/features/registry/profileVersionPage.test.tsx` (US1 acceptance 5, 7, FR-006, FR-007, FR-022)
- [ ] T016 [P] [US1] Create `src/clients/web/src/features/registry/WorkflowsPage.tsx` (workflow versions with id, version, shortened definition digest, creation time, and the default marker derived from `getDefault`) and `src/clients/web/src/features/registry/WorkflowVersionPage.tsx` (requested capabilities, completion policy, segmentation policy, definition digest, created at and by, the node list linked to each profile version, the edge list described with `edgeText`, and the "current default" marker), with `src/clients/web/tests/unit/features/registry/workflowsPage.test.tsx` and `src/clients/web/tests/unit/features/registry/workflowVersionPage.test.tsx` (US1 acceptance 6–7, FR-008, FR-009, FR-022)

### Navigation and routes

- [ ] T017 [US1] Register the read routes `/registry`, `/registry/images`, `/registry/images/:digest`, `/registry/profiles`, `/registry/profiles/:profileId/versions/:version`, `/registry/workflows`, and `/registry/workflows/:workflowId/versions/:version` with `clubAdminOnly` in `src/clients/web/src/router.tsx`; add the `registry` entry after `analystManagers` with `allow: isClubAdmin` to `src/clients/web/src/shell/navItems.ts` and its label `nav.registry` ("Analyst Registry") to `src/clients/web/src/i18n/en/shell.json`; extend `src/clients/web/tests/unit/shell/navigation.test.tsx` so the entry appears only for Club Admins and the routes show the not-permitted view for other members; add to `src/clients/web/src/i18n/en/registry.json` any key that T013–T016 reference but T001 did not create (US1 acceptance 1, FR-001, FR-002)

### Browse browser evidence

- [ ] T018 [US1] Create `src/clients/web/tests/e2e/simulated/registry.spec.ts` with a `Browse` block: a Club Admin reaches the overview in at most two navigation steps from home, sees the default workflow and the "no default" variant, filters images by capability id, opens an image and its manifest text, filters profiles by state, opens a profile version and a workflow version with its node and edge lists, sees raw values for an unknown tier and edge kind, and sees the not-found view for an unknown digest (US1 acceptance 1–7, SC-001)

**Checkpoint**: The read-only Registry area works and is testable on its own.

---

## Phase 4: User Story 2 - Register an Analyst image by digest (Priority: P1)

**Goal**: A Club Admin registers an image by digest reference and understands every refusal.

**Independent Test**: Submitting a tag reference, a reference outside the allowed registry, and a valid digest reference twice shows the client explanation, the platform refusal, the new declaration, and "already registered".

- [ ] T019 [US2] Create `src/clients/web/src/features/registry/RegisterImagePage.tsx` with a reference field that explains before sending when the reference lacks `@sha256:` and 64 hexadecimal characters, a submit that shows progress and disables a second submission, navigation to the image's details on success with "already registered, nothing changed" when `created` is `false`, and a `RefusalAlert` (new `src/clients/web/src/features/registry/RefusalAlert.tsx`, rendering `refusalMessage`, the listed details as text, and "try again later" for retryable refusals) that keeps the entered reference; register `/registry/images/register` with `clubAdminOnly` in `src/clients/web/src/router.tsx`; add a "Register image" link to `src/clients/web/src/features/registry/ImagesPage.tsx`; add `src/clients/web/tests/unit/features/registry/registerImagePage.test.tsx` covering the client check, progress, success, already registered, and every image refusal code (US2 acceptance 1–4, FR-011, FR-012)
- [ ] T020 [US2] Add a `Register image` block to `src/clients/web/tests/e2e/simulated/registry.spec.ts`: tag reference explained without a request, each scripted refusal shown with its message and details, a new registration opening the declaration, and a repeated registration showing "already registered" (US2 acceptance 1–4, SC-003)

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Create, approve, and retire Analyst profile versions (Priority: P2)

**Goal**: A Club Admin creates a profile version from a declaration and approves or retires versions with confirmation.

**Independent Test**: Create a draft from a registered image, see contradictions for a bad value, approve after confirmation, see the refusal for retiring a version of the default workflow, and retire an unused version after confirmation.

### Profile draft and dialogs

- [ ] T021 [P] [US3] Create `src/clients/web/src/features/registry/profileDraft.ts` with `draftFromDeclaration(declaration, existingProfiles)` (capability, image, and model digests fixed; runtime constraints, accelerators, and resources from the manifest; `maxAttempts`, `leaseSeconds`, `staleTimeoutSeconds`, `executionTimeoutSeconds` from the `requirements` minimums; input schema versions from `upstream`; output schema version from `output.schemaVersion`; video policy from `videoAccess`; offered profile ids are those of existing versions with the same capability id, or a new id), with `src/clients/web/tests/unit/features/registry/profileDraft.test.ts` including a declaration without upstream capabilities (FR-013; research.md § R7)
- [ ] T022 [P] [US3] Create `src/clients/web/src/features/registry/ApproveProfileDialog.tsx` and `src/clients/web/src/features/registry/RetireProfileDialog.tsx`: each names the profile id and version and its image and model digests and states its consequence from `contracts/routes-and-roles.md`; approval adds the self-approval note when `createdBy` equals the signed-in member; confirming sends exactly one request with the version's `rowVersion`, cancelling sends nothing, a `RegistryRefusalError` is shown with `RefusalAlert`, and a `RegistryChangedError` shows the "has changed" message with the reloaded state; add `src/clients/web/tests/unit/features/registry/profileDialogs.test.tsx` (US3 acceptance 4–7, FR-014, FR-017)

### Profile creation and actions

- [ ] T023 [US3] Create `src/clients/web/src/features/registry/ProfileCreatePage.tsx` for `/registry/profiles/new?image=<digest>` (an image picker when the parameter is missing) with the form prefilled by `draftFromDeclaration`, read-only capability and image, existing-or-new profile id, list editors for runtime constraints, accelerators, resources, and input schema versions, the created draft or "already existed" outcome, and contradictions shown next to their fields with `RefusalAlert`; register the route with `clubAdminOnly` in `src/clients/web/src/router.tsx`; add "New profile version" to `src/clients/web/src/features/registry/ImagePage.tsx` and `src/clients/web/src/features/registry/ProfilesPage.tsx`, and Approve (draft only) and Retire (draft or approved, only with a known `rowVersion`) with the dialogs to `src/clients/web/src/features/registry/ProfileVersionPage.tsx`; add `src/clients/web/tests/unit/features/registry/profileCreatePage.test.tsx`; add to `src/clients/web/src/i18n/en/registry.json` any key that T021, T022, or this task reference but T001 did not create (US3 acceptance 1–3, FR-013, FR-014)
- [ ] T024 [US3] Add a `Profiles` block to `src/clients/web/tests/e2e/simulated/registry.spec.ts`: create a draft from an image, see "already existed" for identical content, see contradictions, approve with the self-approval note, cancel a confirmation without a request, retire a version used by the default and see the refusal, retire an unused version, and see the "has changed" message for a stale approval (US3 acceptance 1–7, SC-003, SC-006)

**Checkpoint**: Profile governance works through the Web UI.

---

## Phase 6: User Story 4 - Define a workflow version and change the default workflow (Priority: P2)

**Goal**: A Club Admin defines a workflow version from approved profiles and makes it the default with confirmation and optimistic concurrency.

**Independent Test**: Define a version from approved profiles, see a cycle explained, store a valid version, make it the default, and see "has changed" from a stale second session.

### Workflow draft and default dialog

- [ ] T025 [P] [US4] Create `src/clients/web/src/features/registry/workflowDraft.ts` with `emptyDraft(defaultWorkflow)` (segmentation policy prefilled from the default's definition when one exists; completion policy `all-requested-capabilities` preselected), `draftFrom(version, profiles)` (workflow id kept, version identifier cleared, nodes whose profile version is not approved flagged), `requestedCapabilities` defaulting to all node capabilities with subsets allowed, and `checkDraft(draft)` (at least one node, unique node keys, edges and predicates refer to nodes of the draft, no flagged node, positive duration, SHA-256 digest form), with `src/clients/web/tests/unit/features/registry/workflowDraft.test.ts` (FR-015; research.md § R7)
- [ ] T026 [P] [US4] Create `src/clients/web/src/features/registry/SetDefaultWorkflowDialog.tsx` naming the chosen version and the current default (or none), stating that only analysis runs created afterwards use it and existing runs keep their snapshot (and that new runs become possible when no default exists), sending exactly one `setDefault` with the loaded default's `rowVersion` (none when no default was loaded), showing `workflow-not-resolvable` with `RefusalAlert` and `RegistryChangedError` with the current default; add `src/clients/web/tests/unit/features/registry/setDefaultWorkflowDialog.test.tsx` (US4 acceptance 4–7, FR-016, FR-017)

### Workflow definition and default action

- [ ] T027 [US4] Create `src/clients/web/src/features/registry/WorkflowCreatePage.tsx` for `/registry/workflows/new` (optional `fromId` and `fromVersion`) with workflow id and version fields, requested-capability checkboxes, the completion policy choice, segmentation policy fields, a node list editor choosing approved profile versions only, an edge list editor (upstream, dependent, kind, and for conditional edges the predicate kind with a capability or node of the draft), flagged-node messages, `checkDraft` before submission, and violations explained per code and node with `RefusalAlert`; register the route with `clubAdminOnly` in `src/clients/web/src/router.tsx`; add "New version from this" and "Make default" (with the dialog) to `src/clients/web/src/features/registry/WorkflowVersionPage.tsx`, "Define workflow version" to `src/clients/web/src/features/registry/WorkflowsPage.tsx`, and "Change default" to `src/clients/web/src/features/registry/RegistryOverviewPage.tsx`; add `src/clients/web/tests/unit/features/registry/workflowCreatePage.test.tsx`; add to `src/clients/web/src/i18n/en/registry.json` any key that T025, T026, or this task reference but T001 did not create (US4 acceptance 1–3, FR-015, FR-016)
- [ ] T028 [US4] Add a `Workflows` block to `src/clients/web/tests/e2e/simulated/registry.spec.ts`: define a version from approved profiles, start from an existing version with a flagged node that blocks submission, see each scripted violation and `workflow-version-exists` explained, make the stored version the default after confirmation, set a default when none exists, see `workflow-not-resolvable`, and see "has changed" for a stale default (US4 acceptance 1–7, SC-002, SC-003, SC-006)

**Checkpoint**: All Registry changes work through the Web UI.

---

## Phase 7: User Story 5 - Use the area safely in every supported setting (Priority: P3)

**Goal**: The area follows the Web UI's access, accessibility, and online-only rules.

**Independent Test**: Keyboard reachability, axe checks on every view, dialog, and state, offline behavior, and no Registry data in caches or storage.

- [ ] T029 [P] [US5] Create `src/clients/web/tests/e2e/simulated/registry-access.spec.ts`: a Coach and a Viewer see no `Analyst Registry` entry and the not-permitted view on every Registry route; a Club Admin whose role is removed gets the not-permitted view at the next request; axe (`tests/e2e/support/axe.ts`) reports no violations on every view, form, dialog, refusal, empty, and error state; every action is reachable by keyboard (`tests/e2e/support/keyboard.ts`); an offline browser shows the connection-required state and queues nothing; after a session no Registry response is in Cache Storage, IndexedDB, local storage, or session storage (US5 acceptance 1–4, FR-001, FR-020, FR-021, SC-004, SC-005, SC-007)
- [ ] T030 [P] [US5] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts` so requests to `/api/v1/registry/analyst-images`, `/api/v1/registry/analyst-profiles/…`, `/api/v1/registry/workflows/…`, and `/api/v1/registry/default-workflow` are passed to the network and never cached (FR-020)

**Checkpoint**: Every user story is complete and verified in the simulated project.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Platform browser evidence, documentation, and final validation.

- [ ] T031 [P] Create `src/clients/web/tests/e2e/platform/registry.spec.ts` against the AppHost `e2e` mode: sign in as the Club Admin, browse the seeded default workflow and its nodes, create a profile version from a seeded declaration with one execution value changed, approve it, define a workflow version equivalent to the seeded one that uses it, make it the default, register a digest reference of a seeded declaration and see "already registered, nothing changed", and see a tag reference explained; add the chained project `platform-registry` (`testMatch: /registry\.spec\.ts$/` restricted to the platform folder, depending on the last platform project `platform`) to `src/clients/web/playwright.config.ts` so it runs last (FR-023; research.md § R9)
- [ ] T032 [P] Update the architecture and repository documentation so it is true for the merged state: make the narratives and every status statement listed in `## Architecture Updates` of `specs/20261017-185438-web-ui-registry-admin/plan.md` true, re-reading `docs/architecture/` (at least `docs/architecture/client-applications.md`, `docs/architecture/analysts-models-and-hardware.md`, and `docs/architecture/platform-implementation.md`), `AGENTS.md` "Current State", `src/clients/README.md`, and the root `README.md` at implementation time and correcting every statement this feature makes false, including planned, future-tense, or "decided, not yet implemented" wording about the Registry administration, with the plan's rewrites as examples rather than the whole job; run `node .github/scripts/check-markdown.mjs` (constitution Principle I)
- [ ] T033 Run the validation of `specs/20261017-185438-web-ui-registry-admin/quickstart.md`: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, the Host and Registry integration tests, and in `src/clients/web` `npm run build`, `npm test`, and `npm run test:e2e` (all projects), and fix any failure in the files of this feature

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on Setup; T003 (schemas, refreshed snapshot, regenerated client) before T004–T006 and T008; T007 (metadata helper, independent of T003) before T008; T008 before T012. Blocks every user story.
- **US1 (Phase 3)**: depends on Foundational; MVP.
- **US2 (Phase 4)**: depends on Foundational and on T017 (routes and navigation) and T014 (image details) of US1.
- **US3 (Phase 5)**: depends on US1 (it extends `ImagePage.tsx`, `ProfilesPage.tsx`, and `ProfileVersionPage.tsx`) and uses `RefusalAlert.tsx` from US2.
- **US4 (Phase 6)**: depends on US1 (it extends `WorkflowsPage.tsx`, `WorkflowVersionPage.tsx`, and `RegistryOverviewPage.tsx`) and uses `RefusalAlert.tsx` from US2; it needs approved profiles, which the seed provides, so it does not depend on US3.
- **US5 (Phase 7)**: depends on US1–US4 (it checks every view and dialog).
- **Polish (Phase 8)**: depends on all stories.

### Within Each Phase

- Each `###` heading starts a group; consecutive `[P]` tasks under it run in parallel.
- Tasks that register routes in `src/clients/web/src/router.tsx` (T017, T019, T023, T027) or add blocks to `src/clients/web/tests/e2e/simulated/registry.spec.ts` (T018, T020, T024, T028) never run in parallel with each other.

### Parallel Opportunities

- Phase 1: T001, T002 (translations, models).
- Phase 2: T004, T005, T006, T007 (platform tests and the Web UI response metadata); T008, T009, T010, T011 (adapter, helpers, simulated API).
- Phase 3: T013, T014, T015, T016 (read views, each with its own files).
- Phase 5: T021, T022. Phase 6: T025, T026. Phase 7: T029, T030. Phase 8: T031, T032 (platform browser test, documentation), then T033.

---

## Parallel Example: User Story 1

```text
Task: "T013 [P] [US1] RegistryOverviewPage.tsx with registryOverviewPage.test.tsx"
Task: "T014 [P] [US1] ImagesPage.tsx and ImagePage.tsx with their tests"
Task: "T015 [P] [US1] ProfilesPage.tsx and ProfileVersionPage.tsx with their tests"
Task: "T016 [P] [US1] WorkflowsPage.tsx and WorkflowVersionPage.tsx with their tests"
Then: "T017 [US1] routes, navigation entry, and navigation test"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2 (schemas, snapshot, adapter).
2. Complete Phase 3: Club Admins read the stamp's analysis setup without raw API calls.
3. Stop and validate with the `Browse` block and the unit tests.

### Incremental Delivery

1. US1 → read-only Registry area (MVP).
2. US2 → image registration.
3. US3 → profile governance.
4. US4 → workflow definition and default change.
5. US5 → access, accessibility, and online-only evidence; then platform browser evidence and documentation.

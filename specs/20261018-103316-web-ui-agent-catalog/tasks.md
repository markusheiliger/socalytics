---

description: "Task list for Web UI Agent Catalog Administration"
---

# Tasks: Web UI Agent Catalog Administration

**Input**: Design documents from `specs/20261018-103316-web-ui-agent-catalog/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required. FR-023 asks for automated tests of the adapter mapping, every violation and refusal explanation, the form hints, the confirmations with cancellation, self-approval note, and differentiation acknowledgement, the "has changed" handling, role-based access, accessibility rule checks, verbatim prompt text, the online-only rule, and a browser test against the running platform; test work is part of the tasks below.

**Organization**: Tasks are grouped by user story so each story can be implemented and tested independently. All paths are relative to the repository root; Web UI paths start with `src/clients/web/`. The area builds on [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md) and on [Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/spec.md) (its `ResponseMetadataHandler`, `sendWithMetadataFor`, `digest.ts`, `DigestText.tsx`, and `memberNames.ts`), which are merged before this feature is implemented.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; their files never overlap. Parallel tasks never change `src/clients/web/src/i18n/en/agentCatalog.json`, `src/clients/web/src/router.tsx`, or the simulated browser specs; texts a parallel view needs beyond T001 are added by the next task without `[P]` in its phase.
- **[Story]**: Which user story this task belongs to (US1–US6).
- Every task names the files it creates or changes.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: The translation namespace, client models, and form limits every view, form, and adapter uses.

- [x] T001 [P] Create the `agentCatalog` translation namespace `src/clients/web/src/i18n/en/agentCatalog.json` with English texts for every heading, label, action, confirmation, and message of `specs/20261018-103316-web-ui-agent-catalog/contracts/routes-and-roles.md` and every field of `specs/20261018-103316-web-ui-agent-catalog/data-model.md`: overview columns and the offer texts ("Offered in triage conversations: {{version}}", "Not offered: no enabled version", "Not available in conversations yet"), kinds (Coach Agent, Specialist Agent), states (draft, approved, retired), the seven artifact kinds, display names of known providers, routes, escalation and refusal categories, rules, slots, context categories, and the response contract, empty-list texts that say how a list is filled, hint texts (required, remaining length, name pattern, outside the baseline, slot declared but missing or present but undeclared, weights total "{{total}} of 100", whole numbers 0–100, allowed bounds), one message per violation code (`schema-violation` showing the platform's detail, `unknown-tool`, `tool-outside-baseline`, `unknown-agent-id`, `group-mismatch`, `artifact-not-found`, `artifact-kind-mismatch`, `missing-dimension`, `unknown-dimension`, `weight-out-of-range`, `weights-total-not-100`, `rationale-missing`, `dimension-set-not-found`, `dimension-set-incompatible`, `dimension-set-ids-changed`, `modifier-bound-exceeded`, `modifier-movement-exceeded`, `context-category-not-permitted`, `profile-slot-missing`, `profile-slot-not-allowed`, `disclosure-missing`, `safety-rule-missing`, `superseded-profile-invalid`, `provider-not-permitted`), one message per refusal (`agent-catalog-version-exists` "registered versions never change; register a new version", `agent-version-not-draft`, `differentiation-review-required`, `agent-version-not-approved`, `agent-version-enabled` "enable another version or disable the agent first", `agent-version-not-enableable` with reasons `not-approved`, `retired`, `simulated-provider`, `coach-agent-enablement-unsupported`, unknown agent), a generic "refused with code {{code}}" fallback, "already registered, nothing changed", the "has changed" message, the self-approval note, the differentiation acknowledgement, and the three confirmation texts; register the namespace in `ns` and `resources` of `src/clients/web/src/i18n/index.ts` (FR-003, FR-012–FR-017, FR-021, FR-022)
- [x] T002 [P] Create the application-owned models of `specs/20261018-103316-web-ui-agent-catalog/data-model.md` in `src/clients/web/src/platform/models/agentCatalog.ts`: `CatalogBaseline`, `BaselineAgent`, `CatalogAgentSummary`, `Enablement` (with `etag`), `Offer` (`offered` with `version` or `notOffered` with `reason: 'noEnabledVersion' | 'coachNotAvailable'`), `AgentVersionSummary`, `AgentVersion` (with `etag`), `ArtifactRef`, `ArtifactSummary`, `ArtifactRecord<K>`, `Distance`, `MemberRef`, the per-kind documents `DocumentOf<K>` of the data model's document table, the drafts `ArtifactDraft<K>`, `ProfileDraft`, `AgentVersionDraft`, and the `CatalogOutcome` union (`created`, `replayed`, `validationFailed` with `violations: [{ code, pointer, detail, location }]`, `versionExists`, `transitionRefused`, `enablementRefused` with `code` and `reason`, `changed`); every enumerated value is a union of its known values and `string` ("unknown enumeration values are kept as raw strings") (FR-022)
- [x] T003 [P] Create `src/clients/web/src/features/agent-catalog/catalogLimits.ts` with the enumerations and limits of `specs/20261018-103316-web-ui-agent-catalog/research.md` § R8 (response contract `agent-response/1`; slots `agent_id`, `display_name`, `philosophy_profile`, `tool_policy`, `evidence_context`; providers `openai`, `github-models`, `simulated`; name pattern `^[A-Za-z0-9._/:-]{1,128}$`; `temperature` 0–2, `topP` 0–1, `maxOutputTokens` 1–32,768, `reasoningEffort` `low`|`medium`|`high`; routes `user-to-coach`, `user-to-specialist`, `coach-to-specialist`; the eight categories; the rules; prompt text "1–32,000 characters"; rationale and provenance "≤1,000"; disclosure "20–1,000 characters"; hard boundaries "1–20 strings up to 500 characters"; dimensions "1–32"; weights "0–100 integer", total 100; `perDimensionBound` "1–3"; `maxPositiveMovement` "1–10"; context categories `score-state`, `match-period`, `player-number-state`, `opponent-phase-behavior`, `evidence-availability`) and `src/clients/web/tests/unit/features/agent-catalog/catalogLimits.test.ts`, which reads the JSON Schemas under `contracts/agent-catalog/*/v1/` and asserts that every enumeration and limit equals the contract (FR-010)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: OpenAPI document schemas, pure helpers, the catalog adapter and hooks, shared components, the simulated API, and the navigation entry.

**⚠️ CRITICAL**: No user story work can begin until this phase is complete.

### OpenAPI document schemas

- [x] T004 Check the committed snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` for the operations of `specs/20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-openapi.md`; where a record document or agent version document is described as a free-form object, create `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogDocumentContracts.cs` with schema-only records for the seven kind documents (each with exactly the properties of `contracts/agent-catalog/<kind>/v1/<kind>.schema.json`) joined as the polymorphic `AgentCatalogRecordDocument` with discriminator `kind`, and `AgentVersionDocument`, and attach them in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentCatalogEndpoints.cs` as `.Accepts<…>("application/json")` and `.Produces<…>()` metadata only, so request bodies are still read as raw JSON and validated through `IContractValidator` and responses still write the stored canonical document unchanged; add any missing problem schema for `violations` and `reason`; refresh the snapshot with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests`, check that only these schemas changed, and run `npm run build` in `src/clients/web` so the regenerated (ignored) Kiota client contains the models (FR-019; research.md § R1)

### Schema evidence and pure helpers

- [x] T005 [P] Create `src/platform/Tests/SocAlytics.Platform.Host.Tests/AgentCatalogOpenApiTests.cs`: for every operation of `specs/20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-openapi.md`, the running API's OpenAPI document declares an `application/json` response schema for each success status and a request schema where the operation has a body, and each kind's document schema and the agent version document schema have exactly the property names of their JSON Schema contracts under `contracts/agent-catalog/` (FR-019)
- [x] T006 [P] Create `src/clients/web/src/features/agent-catalog/versionProposal.ts` (parse SemVer 2.0.0 without build metadata; propose the next patch version above the highest registered version; `0.1.0` for a new identifier) and `src/clients/web/tests/unit/features/agent-catalog/versionProposal.test.ts` (FR-009; research.md § R7)
- [x] T007 [P] Create `src/clients/web/src/features/agent-catalog/violationLocation.ts`, mapping a violation's JSON Pointer to a form location (top-level field, list entry, profile row and column, the `/weights` total, agent version reference, or the summary for unknown and empty pointers) as research.md § R3 describes, and `src/clients/web/tests/unit/features/agent-catalog/violationLocation.test.ts` covering every pointer form there (FR-012)
- [x] T008 [P] Create `src/clients/web/src/features/agent-catalog/offer.ts`, composing `CatalogAgentSummary` from the baseline, version list, enablements, and available agents with `Offer` as research.md § R6 describes, and `src/clients/web/tests/unit/features/agent-catalog/offer.test.ts` (offered, no enabled version, Coach Agent not available, Coach Agent listed by the platform, state counts) (FR-003)
- [x] T009 [P] Create `src/clients/web/src/features/agent-catalog/outcomeText.ts`, returning the translation key and parameters for every violation code, refusal code, and enablement reason of T001 and the raw-code fallback, and `src/clients/web/tests/unit/features/agent-catalog/outcomeText.test.ts`, asserting a distinct key for every known code and that each key exists in `src/clients/web/src/i18n/en/agentCatalog.json` (FR-012, FR-016, FR-022; SC-003)

### Catalog adapter and hooks

- [x] T010 Create `src/clients/web/src/platform/adapters/agentCatalogAdapter.ts` with every function of `specs/20261018-103316-web-ui-agent-catalog/contracts/agent-catalog-adapter.md` over the generated client, using `sendWithMetadataFor` for `ETag` and `201`/`200`, mapping refusals and stale states to the outcomes of research.md § R4 (validation violations with `location` from `violationLocation.ts`), leaving `401`, `403`, offline, `429`, and `5xx` to `toPlatformFailure`, passing documents through unchanged, and resolving member ids through `src/clients/web/src/features/registry/memberNames.ts` (`system:seed` as the development seed); and `src/clients/web/tests/unit/platform/adapters/agentCatalogAdapter.test.ts` covering the mapping of the baseline, agents, versions, enablements, available agents, every kind's document, distances, every outcome, `If-Match` values, and unknown values kept raw (FR-019, FR-022, FR-023)
- [x] T011 Create `src/clients/web/src/features/agent-catalog/agentCatalogKeys.ts` with the TanStack Query keys and hooks over `agentCatalogAdapter` (in-memory only; invalidation of the overview, agent, version, enablement, and artifact lists after a change) (FR-020)

### Shared components and simulated API

- [x] T012 [P] Create `src/clients/web/src/features/agent-catalog/CatalogText.tsx` (verbatim, pre-formatted, bounded, scrollable plain text through React text nodes, no markup interpretation) and `src/clients/web/src/features/agent-catalog/ViolationSummary.tsx` (violations without a known location above a form, announced to assistive technology), with `src/clients/web/tests/unit/features/agent-catalog/CatalogText.test.tsx` (markup and `{{…}}` placeholders shown verbatim) and `src/clients/web/tests/unit/features/agent-catalog/ViolationSummary.test.tsx` (FR-012, FR-021)
- [x] T013 [P] Extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with the agent catalog operations of `specs/archive/20261018-101517-agent-catalog-registry/contracts/agent-catalog-api.md` and in-memory state seeded like the Development and Test seed (nine baseline agents, `specialist-tactics` and `specialist-video-analysis` `0.1.0` approved and enabled with their four artifacts), returning `ETag`, `201`/`200` replays, every validation problem and refusal, `412`/`428` for stale `If-Match`, and controls to inject agent versions in any state registered by any member, Coach Agent artifacts, distances, and stale changes (FR-023)
- [x] T014 [P] Create `src/clients/web/tests/unit/features/agent-catalog/fixtures.ts` with shared view-test fixtures (baseline, agents, versions, enablements, one record of every kind, distances with and without `reviewRequired`) and a fake `agentCatalogAdapter` (FR-023)

### Navigation entry

- [ ] T015 Add the `agentCatalog` entry (`/agent-catalog`, `allow: isClubAdmin`, directly after `registry`) to `src/clients/web/src/shell/navItems.ts`, the label `nav.agentCatalog` ("Agent catalog") to `src/clients/web/src/i18n/en/shell.json`, and cases to `src/clients/web/tests/unit/shell/navigation.test.tsx` that Club Admins see the entry and Coaches, Viewers, and Registrars do not, and extend every existing test that pins the navigation so the unchanged full verification passes: insert `'agentCatalog'` directly after `'registry'` in the expected `Object.keys(shell.nav)` list of `src/clients/web/tests/unit/i18n/keys.test.ts`, and add `'Agent catalog'` directly after `'Analyst Registry'` in the `ClubAdmin` list of `NAVIGATION` in `src/clients/web/tests/e2e/simulated/navigation.spec.ts` and `src/clients/web/tests/e2e/platform/navigation.spec.ts` (never loosen an assertion) (FR-001; SC-004)

**Checkpoint**: Foundation ready; user story implementation can begin.

---

## Phase 3: User Story 1 - See the agent catalog and what conversations offer (Priority: P1) 🎯 MVP

**Goal**: Club Admins browse agents, versions, the baseline, and Specialist Agent artifacts read-only, and see what triage conversations offer.

**Independent Test**: With the seeded catalog, a Club Admin sees the nine agents with the two enabled versions offered, opens `specialist-tactics`, `0.1.0`, and its four artifacts with every listed field and the prompt text verbatim; a Coach gets the not-permitted view.

### Read views

- [ ] T016 [P] [US1] Create `src/clients/web/src/features/agent-catalog/AgentCatalogOverviewPage.tsx` (every baseline agent with identifier, kind, domain, display name, enabled version or "none", state counts, and the offer from `offer.ts`; links to the agent, the artifact lists, and the baseline; loading, empty, and error states) and `src/clients/web/tests/unit/features/agent-catalog/AgentCatalogOverviewPage.test.tsx` (FR-003; SC-001)
- [ ] T017 [P] [US1] Create `src/clients/web/src/features/agent-catalog/CatalogBaselinePage.tsx` (tool allowlist and per-agent tool baselines, read-only) and `src/clients/web/tests/unit/features/agent-catalog/CatalogBaselinePage.test.tsx` (FR-007)
- [ ] T018 [P] [US1] Create `src/clients/web/src/features/agent-catalog/AgentPage.tsx` (versions with version, state, shortened digest through `src/clients/web/src/features/registry/DigestText.tsx`, registration time, and enabled marker, filterable by the `state` search parameter; tool baseline; current enablement with who changed it and when; "no version registered" with a link to register one) and `src/clients/web/tests/unit/features/agent-catalog/AgentPage.test.tsx` (FR-004, FR-008)
- [ ] T019 [P] [US1] Create `src/clients/web/src/features/agent-catalog/AgentVersionPage.tsx` (state, display name, digest, disclosure for Coach Agents, every artifact reference linked to its artifact page, registering, approving, and retiring members and times, self-approval and differentiation-acknowledgement markers, enabled marker) and `src/clients/web/src/features/agent-catalog/artifacts/DistanceList.tsx` (distances with every `reviewRequired` distance marked; "no distance applies" when empty), with `src/clients/web/tests/unit/features/agent-catalog/AgentVersionPage.test.tsx` and `src/clients/web/tests/unit/features/agent-catalog/DistanceList.test.tsx` (FR-005, FR-008)
- [ ] T020 [P] [US1] Create `src/clients/web/src/features/agent-catalog/ArtifactsPage.tsx` (records of one `:kind` with identifier, version, shortened digest, and registration time, filterable by the `id` search parameter, a kind switcher, and the empty text) and `src/clients/web/tests/unit/features/agent-catalog/ArtifactsPage.test.tsx` (FR-006)
- [ ] T021 [P] [US1] Create the read-only views `src/clients/web/src/features/agent-catalog/artifacts/PromptTemplateView.tsx` (group, response contract, slots, `dimensionSetMajor`, full text through `CatalogText`), `ModelSelectionView.tsx` (provider, model, deployment, parameters), `ToolPolicyView.tsx` (tools as a list, each marked within or outside the baseline of the agent in context), and `SafetyPolicyView.tsx` (group, routes, hard boundaries through `CatalogText`, escalation and refusal categories, rules, each in words or raw) in the same folder, with one test file each under `src/clients/web/tests/unit/features/agent-catalog/` (FR-006, FR-007, FR-021, FR-022)

### Routes and browsing evidence

- [ ] T022 [US1] Create `src/clients/web/src/features/agent-catalog/ArtifactPage.tsx`, dispatching `:kind` to the T021 views and showing kinds without a view yet as their raw document through `CatalogText`, with not-found handling for an unknown kind, an identifier outside the kebab-case form, or a version that is not SemVer (no request sent); add the routes `/agent-catalog`, `/agent-catalog/baseline`, `/agent-catalog/agents/:agentId`, `/agent-catalog/agents/:agentId/versions/:version`, `/agent-catalog/artifacts/:kind`, and `/agent-catalog/artifacts/:kind/:artifactId/versions/:version` wrapped in `clubAdminOnly` to `src/clients/web/src/router.tsx`; add texts the T016–T021 views needed beyond T001 to `src/clients/web/src/i18n/en/agentCatalog.json`; and `src/clients/web/tests/unit/features/agent-catalog/ArtifactPage.test.tsx` (FR-001, FR-002, FR-006)
- [ ] T023 [US1] Create `src/clients/web/tests/e2e/simulated/agent-catalog.spec.ts` with browsing scenarios against the simulated API: overview with the two offered specialists, agent, version, each of the four artifacts with every field, prompt text verbatim, digest copy, state filter, and not-found addresses (US1 acceptance scenarios 1–7; FR-023)

**Checkpoint**: User Story 1 is functional and testable on its own.

---

## Phase 4: User Story 2 - Enable, change, or disable an agent's version for the stamp (Priority: P1)

**Goal**: Club Admins change or remove an agent's enabled version with confirmation and optimistic concurrency.

**Independent Test**: Enable a second approved `specialist-tactics` version after confirmation, see the offer updated, disable and re-enable, receive "enablement changed" from a stale session, and see a draft's enable refusal explained.

- [ ] T024 [US2] Create `src/clients/web/src/features/agent-catalog/ChangeEnablementDialog.tsx` stating what `specs/20261018-103316-web-ui-agent-catalog/contracts/routes-and-roles.md` § Confirmations lists for enabling, changing, and disabling (including the Coach Agent note), sending exactly one `setEnablement` with the last seen `etag`, showing `enablementRefused` with its reason and `changed` with the current enablement, and never repeating the request; and `src/clients/web/tests/unit/features/agent-catalog/ChangeEnablementDialog.test.tsx` (confirm, cancel sends nothing, every refusal and reason, stale) (FR-016, FR-017)
- [ ] T025 [US2] Wire "Change enabled version" and "Disable" into `src/clients/web/src/features/agent-catalog/AgentPage.tsx` and "Enable" for every approved version of either group into `src/clients/web/src/features/agent-catalog/AgentVersionPage.tsx`, refreshing the overview's offer after a change; add any further texts to `src/clients/web/src/i18n/en/agentCatalog.json`; extend `src/clients/web/tests/unit/features/agent-catalog/AgentPage.test.tsx` and `AgentVersionPage.test.tsx` (FR-016, FR-017)
- [ ] T026 [US2] Extend `src/clients/web/tests/e2e/simulated/agent-catalog.spec.ts` with enablement scenarios: enable a second approved version, disable, re-enable, cancel, refusals for a draft, a retired version, the simulated provider, and a Coach Agent version, and a stale change from a second page (US2 acceptance scenarios 1–6; SC-006)

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Register Specialist Agent artifacts and versions (Priority: P2)

**Goal**: Club Admins register prompt templates, model selections, tool and safety policies, and agent versions from forms whose hints never replace the platform's validation.

**Independent Test**: Register a new prompt template version from the seeded one, see a tool policy with an out-of-baseline tool refused next to the tool list, register agent version `0.2.0` from `0.1.0`, and see "already registered, nothing changed" for a repeated registration.

### Specialist Agent artifact forms

- [ ] T027 [P] [US3] Create `src/clients/web/src/features/agent-catalog/artifacts/PromptTemplateForm.tsx` (group, response contract, slots as choices from `catalogLimits.ts`, `dimensionSetMajor` for `coach`, text with remaining length of "1–32,000 characters" and the hint comparing `{{slot}}` placeholders in the text with the declared slots; violations at their locations) and `src/clients/web/tests/unit/features/agent-catalog/PromptTemplateForm.test.tsx` (FR-009, FR-010, FR-012)
- [ ] T028 [P] [US3] Create `src/clients/web/src/features/agent-catalog/artifacts/ModelSelectionForm.tsx` (provider choice, model and deployment with the name-pattern hint `^[A-Za-z0-9._/:-]{1,128}$`, optional parameters within their ranges; no field for credentials, endpoints, or other secrets) and `src/clients/web/tests/unit/features/agent-catalog/ModelSelectionForm.test.tsx` (FR-009, FR-010)
- [ ] T029 [P] [US3] Create `src/clients/web/src/features/agent-catalog/artifacts/ToolPolicyForm.tsx` (tools chosen only from the baseline's allowlist, "1–11 unique allowlisted identifiers", marking tools outside the baseline of the agent named by the `agentId` search parameter as a hint) and `src/clients/web/tests/unit/features/agent-catalog/ToolPolicyForm.test.tsx` (FR-009, FR-010)
- [ ] T030 [P] [US3] Create `src/clients/web/src/features/agent-catalog/artifacts/SafetyPolicyForm.tsx` (group, routes, hard boundaries "1–20 strings up to 500 characters", escalation and refusal categories, rules, with the Coach Agent rules `simulation-disclosure`, `non-impersonation`, `non-fabrication` hinted for `coach`) and `src/clients/web/tests/unit/features/agent-catalog/SafetyPolicyForm.test.tsx` (FR-009, FR-010)

### Registration pages

- [ ] T031 [US3] Create `src/clients/web/src/features/agent-catalog/ArtifactCreatePage.tsx`, dispatching `:kind` to the T027–T030 forms, starting from `?fromId=&fromVersion=` with every field prefilled, proposing the version through `versionProposal.ts`, preventing a second submission while one is in progress, and handling `created` (open the record), `replayed` ("already registered, nothing changed"), `versionExists`, and `validationFailed` (violations at their locations and the rest in `ViolationSummary`) while keeping every entered value; add "New version from this" to `src/clients/web/src/features/agent-catalog/ArtifactPage.tsx`, the route `/agent-catalog/artifacts/:kind/new` to `src/clients/web/src/router.tsx`, and further texts to `src/clients/web/src/i18n/en/agentCatalog.json`; with `src/clients/web/tests/unit/features/agent-catalog/ArtifactCreatePage.test.tsx` (FR-009, FR-012, FR-013)
- [ ] T032 [US3] Create `src/clients/web/src/features/agent-catalog/AgentVersionCreatePage.tsx` (agent from the route fixes the group; display name prefilled from the baseline and editable; reference pickers per required kind from registered artifacts, prompt templates and safety policies only of the agent's group; `?from=<version>` prefill; proposed version; outcomes as in T031 with violations at `/artifacts/*`); add "New version" to `src/clients/web/src/features/agent-catalog/AgentPage.tsx` and `AgentVersionPage.tsx`, the route `/agent-catalog/agents/:agentId/versions/new` to `src/clients/web/src/router.tsx`, and further texts to `src/clients/web/src/i18n/en/agentCatalog.json`; with `src/clients/web/tests/unit/features/agent-catalog/AgentVersionCreatePage.test.tsx` (FR-009, FR-011, FR-012, FR-013)
- [ ] T033 [US3] Extend `src/clients/web/tests/e2e/simulated/agent-catalog.spec.ts` with registration scenarios: prompt template from the seeded one with proposed `0.1.1`, an out-of-baseline tool policy refused next to the tool list, a model selection with an invalid name refused next to the field, an unlocated violation in the summary, a replay, an existing version with different content, and agent version `0.2.0` from `0.1.0` shown as draft (US3 acceptance scenarios 1–7; SC-003)

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Approve and retire agent versions (Priority: P2)

**Goal**: Club Admins approve drafts and retire approved versions with confirmations, self-approval note, and differentiation acknowledgement.

**Independent Test**: Approve a self-registered draft (self-approval note), see retiring the enabled version refused, retire a non-enabled approved version, and receive "version changed" from a stale session.

### Lifecycle dialogs

- [ ] T034 [P] [US4] Create `src/clients/web/src/features/agent-catalog/ApproveAgentVersionDialog.tsx` (agent, version, digest, artifact digests; approved versions never change; an accountable decision, not evaluation evidence; the self-approval note when `registeredBy` is the signed-in member; every `reviewRequired` distance through `DistanceList` with a required acknowledgement that sends `differentiationReviewAcknowledged: true`; one request with the last seen `etag`; `transitionRefused` and `changed` shown, never repeated) and `src/clients/web/tests/unit/features/agent-catalog/ApproveAgentVersionDialog.test.tsx` (FR-014, FR-017)
- [ ] T035 [P] [US4] Create `src/clients/web/src/features/agent-catalog/RetireAgentVersionDialog.tsx` (retired versions can no longer be enabled while invocations that pinned them keep them; `agent-version-enabled` explained with the advice to enable another version or disable the agent first; one request with the last seen `etag`; `changed` shown) and `src/clients/web/tests/unit/features/agent-catalog/RetireAgentVersionDialog.test.tsx` (FR-015, FR-017)

### Lifecycle wiring

- [ ] T036 [US4] Wire "Approve" for drafts and "Retire" for approved versions into `src/clients/web/src/features/agent-catalog/AgentVersionPage.tsx`, showing the approval or retirement with member and time afterwards; add further texts to `src/clients/web/src/i18n/en/agentCatalog.json`; extend `src/clients/web/tests/unit/features/agent-catalog/AgentVersionPage.test.tsx` (FR-014, FR-015)
- [ ] T037 [US4] Extend `src/clients/web/tests/e2e/simulated/agent-catalog.spec.ts` with lifecycle scenarios: approve with the self-approval note, cancel, retire the enabled version refused, retire a non-enabled version, approve a non-draft refused, and stale approval and retirement from a second page (US4 acceptance scenarios 1, 3–5; SC-006)

**Checkpoint**: User Stories 1–4 work independently.

---

## Phase 7: User Story 5 - Read and register Coach Agent philosophy artifacts (Priority: P3)

**Goal**: Club Admins read and register dimension sets, base philosophy profiles as tables, modifier policies, and Coach Agent versions.

**Independent Test**: Register `coach-philosophy-dimensions` 1.0.0, see a 99-point profile's hint and refusal next to the weights, store a 100-point profile with distances, see a bound of 4 refused, and register and approve a Coach Agent version with its disclosure.

### Coach Agent artifact views and forms

- [ ] T038 [P] [US5] Create `src/clients/web/src/features/agent-catalog/artifacts/DimensionSetView.tsx` and `DimensionSetForm.tsx` (dimensions "1–32 `{ id, meaning, acceptedEvidence: [tool ids] }`, unique kebab-case ids", evidence tools from the allowlist) with one test file each under `src/clients/web/tests/unit/features/agent-catalog/` (FR-006, FR-009, FR-010)
- [ ] T039 [P] [US5] Create `src/clients/web/src/features/agent-catalog/artifacts/PhilosophyProfileView.tsx` (dimension-set identifier and version, a table of every dimension with meaning, weight, rationale, and provenance in the dimension set's order, the total, author, reviewers, superseded version linked, distances through `DistanceList`) and `PhilosophyProfileForm.tsx` (dimension-set version choice generating one row per dimension, weights accepting whole numbers with a hint for any weight outside 0–100, rationale and provenance "≤1,000", running total with a hint while it is not 100 without blocking submission, author, architecture and football-domain reviewers "each at least one", optional supersedes; changing the dimension set keeps values of matching ids and drops the others with a notice; violations at their rows and at the total), with one test file each under `src/clients/web/tests/unit/features/agent-catalog/` (FR-006, FR-009, FR-010, FR-012)
- [ ] T040 [P] [US5] Create `src/clients/web/src/features/agent-catalog/artifacts/ModifierPolicyView.tsx` and `ModifierPolicyForm.tsx` (`dimensionSetMajor`, `perDimensionBound` "1–3", `maxPositiveMovement` "1–10", context categories, `budgetNeutral` shown as the budget-neutral rule) with one test file each under `src/clients/web/tests/unit/features/agent-catalog/` (FR-006, FR-009, FR-010)

### Coach Agent wiring

- [ ] T041 [US5] Register the T038–T040 views in `src/clients/web/src/features/agent-catalog/ArtifactPage.tsx` and forms in `ArtifactCreatePage.tsx` (profiles show their distances after registration); add the Coach Agent fields to `src/clients/web/src/features/agent-catalog/AgentVersionCreatePage.tsx` (required disclosure "20–1,000 characters", profile and modifier-policy pickers with those matching the prompt template's `dimensionSetMajor` listed first as a hint); show "not available in conversations yet" for approved Coach Agent versions in `AgentVersionPage.tsx`; add further texts to `src/clients/web/src/i18n/en/agentCatalog.json`; extend the matching unit tests under `src/clients/web/tests/unit/features/agent-catalog/` (FR-005, FR-006, FR-011)
- [ ] T042 [US5] Create `src/clients/web/tests/e2e/simulated/agent-catalog-coach.spec.ts`: dimension set registration, a 99-point profile with hint and located refusal, a stored 100-point profile with distances, a modifier policy with bound 4 refused, a Coach Agent version without disclosure refused, approval with the differentiation acknowledgement for a close profile, and the Coach Agent enable refusal explained (US5 acceptance scenarios 1–5, US4 acceptance scenario 2, US2 acceptance scenario 5)

**Checkpoint**: User Stories 1–5 work independently.

---

## Phase 8: User Story 6 - Use the area safely in every supported setting (Priority: P3)

**Goal**: The area meets the Web UI's access, accessibility, verbatim-text, and online-only rules.

**Independent Test**: Browser tests reach every view and action by keyboard with no accessibility violations, show markup in prompt text verbatim, find no catalog data in caches or storage, and show the connection-required state offline.

- [ ] T043 [P] [US6] Create `src/clients/web/tests/e2e/simulated/agent-catalog-access.spec.ts`: Coaches, Viewers, and Registrars see no navigation entry and the not-permitted view on every Agent Catalog address without a catalog request; a `403` after role removal leads to the not-permitted view; axe checks with `src/clients/web/tests/e2e/support/axe.ts` on every view, form, dialog, and state; keyboard reachability with `src/clients/web/tests/e2e/support/keyboard.ts`; a prompt with markup and `{{…}}` placeholders shown verbatim; no catalog response or form content in Cache Storage, IndexedDB, local storage, or session storage after a session; the connection-required state offline with nothing queued (US6 acceptance scenarios 1–4; FR-001, FR-020, FR-021; SC-004, SC-005, SC-007)
- [ ] T044 [P] [US6] Extend `src/clients/web/tests/unit/serviceWorker/shellWorker.test.ts` with cases that `/api/v1/agent-catalog/*` requests stay unhandled by the service worker (FR-020)

**Checkpoint**: All user stories are complete.

---

## Phase 9: Polish & Cross-Cutting Concerns

**Purpose**: Platform browser evidence, documentation, and full verification.

- [ ] T045 Create `src/clients/web/tests/e2e/platform/agent-catalog.spec.ts` and the chained project `platform-agent-catalog` in `src/clients/web/playwright.config.ts`, running after the last platform project, against the AppHost `e2e` mode: browse the seeded catalog, register prompt template `0.1.1` from the seeded `specialist-tactics` prompt, register agent version `0.2.0` from `0.1.0` with it, approve it (self-approval note), enable it, and see that triage conversations offer `0.2.0` (FR-023; research.md § R11)
- [ ] T046 Make `docs/architecture/` true for the merged state as the plan's Architecture Updates section requires: re-read `docs/architecture/` (including `client-applications.md`, `intelligence-agent-catalog.md`, `platform-implementation.md`, `README.md`, and `overview.md`) and turn every narrative and status statement this feature affects into the implemented state — at least the agent catalog administration in Client Applications → Web UI, the operator sentence in Intelligence Agent Catalog → Prompt And Advice Governance and in Platform Implementation → Agent Runtime, the remaining-future-work list of Agent Runtime, Acceptance Evidence, and Architecture Reassessment — while keeping every other decision status unchanged; and keep `AGENTS.md` "Current State", `src/clients/README.md`, and the root `README.md` consistent (the listed rewrites are examples, not the whole job)
- [ ] T047 Run the quickstart's automated checks from `specs/20261018-103316-web-ui-agent-catalog/quickstart.md`: `dotnet restore src/platform/SocAlytics.Platform.slnx`, `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, and in `src/clients/web` `npm ci`, `npm run build`, `npm test`, `npm run test:e2e -- --project simulated`, and `npm run test:e2e`; then `node .github/scripts/check-markdown.mjs`; fix what fails within this feature's scope

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies; needs [Agent Catalog Registry](../archive/20261018-101517-agent-catalog-registry/spec.md) and [Web UI Analyst Registry Administration](../archive/20261017-185438-web-ui-registry-admin/spec.md) merged.
- **Foundational (Phase 2)**: depends on Setup; blocks all user stories.
- **US1 (Phase 3)**: depends on Foundational.
- **US2 (Phase 4)**: depends on US1 (it adds actions to `AgentPage.tsx` and `AgentVersionPage.tsx`).
- **US3 (Phase 5)**: depends on US1 (`ArtifactPage.tsx`, routes); independent of US2.
- **US4 (Phase 6)**: depends on US1; its browser test registers nothing (it uses simulated drafts); independent of US2 and US3.
- **US5 (Phase 7)**: depends on US1, US3 (`ArtifactCreatePage.tsx`, `AgentVersionCreatePage.tsx`), and US4 (the approval dialog's differentiation acknowledgement, which T042 exercises).
- **US6 (Phase 8)**: depends on every view existing (US1–US5).
- **Polish (Phase 9)**: depends on all user stories.

### Within Each Phase

- Consecutive `[P]` tasks under one heading run in parallel from the same commit; the next task without `[P]` runs after the whole group.
- Tasks that change `agentCatalog.json`, `router.tsx`, or a simulated browser spec never carry `[P]`.

### Parallel Opportunities

- Phase 1: T001, T002, T003.
- Phase 2: T005–T009 (Host test and four pure helpers); T012–T014 (components, simulated API, fixtures).
- Phase 3: T016–T021 (six read views).
- Phase 5: T027–T030 (four forms).
- Phase 6: T034, T035 (two dialogs).
- Phase 7: T038–T040 (three Coach Agent view and form pairs).
- Phase 8: T043, T044.

---

## Parallel Example: User Story 1

```text
T016 AgentCatalogOverviewPage.tsx + test
T017 CatalogBaselinePage.tsx + test
T018 AgentPage.tsx + test
T019 AgentVersionPage.tsx + DistanceList.tsx + tests
T020 ArtifactsPage.tsx + test
T021 PromptTemplateView, ModelSelectionView, ToolPolicyView, SafetyPolicyView + tests
then T022 (ArtifactPage, routes, texts) and T023 (simulated browsing spec)
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Phase 1 and Phase 2.
2. Phase 3 (US1): Club Admins see the catalog and what conversations offer.
3. Stop and validate with T023.

### Incremental Delivery

1. US1 → browse (MVP).
2. US2 → change the enabled version (the main operator outcome).
3. US3 → register Specialist Agent artifacts and versions.
4. US4 → approve and retire.
5. US5 → Coach Agent philosophy artifacts.
6. US6 → access, accessibility, verbatim text, online only.
7. Polish → platform browser test, documentation, full verification.

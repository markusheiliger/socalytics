---

description: "Task list for Web UI Analyst Manager Fleet and Registration Approval"
---

# Tasks: Web UI Analyst Manager Fleet and Registration Approval

**Input**: Design documents from `specs/archive/20261013-122132-web-ui-manager-fleet/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/routes-and-roles.md](contracts/routes-and-roles.md), [contracts/platform-adapter.md](contracts/platform-adapter.md), [contracts/platform-changes.md](contracts/platform-changes.md), [quickstart.md](quickstart.md)

**Tests**: Required. The spec requires automated coverage of every acceptance scenario (SC-003) and the constitution requires focused tests. Every task adds the tests for the code it creates in the same task, so the platform test suites, `npm run build`, `npm test`, and `npm run test:e2e` stay green after each task. Browser tests in the `simulated` project use the foundation's fixtures (axe WCAG 2.2 AA on every view, failure on any CSP violation, no secrets in browser storage).

**Organization**: Tasks are grouped by user story. Merged before this feature starts (do not re-create): Web UI Foundation `specs/archive/20261012-150657-web-ui-foundation` (the `src/clients/web/` project, session and guards, shell and navigation, the platform adapters and `PlatformFailure`, `simulatedApi.ts` and fixtures, the OpenAPI snapshot with `OpenApiSnapshotTests`, the AppHost Web UI resource and `e2e` mode), Analyst Manager Status Report and Fleet Read API `specs/archive/20261012-153308-manager-fleet-status` (the `status` and `connectivity` fields, the Registrar-scoped read, the self status report), Analyst Manager Registration `specs/archive/20261005-130704-analyst-manager-registration` (the registration operations, `TestAnalystManager`, the golden fixtures in `contracts/analyst-manager/registration/v1/`), and the Web UI verification environment `specs/archive/20261012-145505-web-ui-environment`. No migration and no new dependency.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks); consecutive `[P]` tasks under one heading run in parallel, each from the same commit
- **[Story]**: Which user story this task belongs to (US1–US4)
- Every task names its exact file paths

## Path Conventions

- Web UI project: `src/clients/web/` with `src/` (application), `tests/unit/` (Vitest), `tests/e2e/simulated/`, `tests/e2e/platform/`, `tests/e2e/support/` (Playwright).
- Platform: `src/platform/SocAlytics.Platform.Application/Registry/`, `src/platform/SocAlytics.Platform.Api/Registry/`, `src/platform/SocAlytics.Platform.Api/openapi/v1.json`, `src/platform/SocAlytics.Platform.AppHost/`, `src/platform/Tests/`.

---

## Phase 1: Setup (Platform Changes)

**Purpose**: The two minimal platform changes of contracts/platform-changes.md.

- [x] T001 [P] Verification base address (contracts/platform-changes.md § Verification base address; research.md § R1; FR-001, FR-017): add the optional `VerificationBaseUri` to the pairing options in `src/platform/SocAlytics.Platform.Application/Registry/RegistryOptions.cs`; make `VerificationUri` in `src/platform/SocAlytics.Platform.Application/Registry/StampOptions.cs` build `VerificationBaseUri ?? PublicBaseUri` + `VerificationPath`; validate it at start in `src/platform/SocAlytics.Platform.Api/Registry/RegistryApiRegistration.cs` ("Absolute origin (no path, query, or fragment)"; "`https` outside Development; `http` or `https` in Development"); set `Registry__Pairing__VerificationBaseUri` to the Web UI endpoint's origin in `src/platform/SocAlytics.Platform.AppHost/Program.cs` in the default and the `e2e` mode. Tests: `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry/VerificationBaseUriTests.cs` (invalid values refused at start; the pairing response's `verificationUri` uses the base when set and `Stamp:PublicBaseUri` otherwise; token, activation, and self URIs unchanged) and an assertion of the AppHost setting in `src/platform/Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs`.
- [x] T002 [P] Typed registration operations (contracts/platform-changes.md § Typed registration operations; research.md § R2; FR-016): add `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationContracts.cs` with `AnalystManagerRegistrationDto` and its nested records and `AnalystManagerRegistrationPageDto`, with exactly the property names, nullability, and serialization of the current anonymous objects (including `status` and `connectivity`); move `PairingCodeSubmissionBody` and `ReasonRequest` there; in `src/platform/SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs` return the records and declare `.Accepts<>`, `.Produces<>`, and the `.ProducesProblem` codes of the contract table for the six operations, after confirming each code from the handlers and existing tests; regenerate `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with the foundation's snapshot update command. Tests: first add `src/platform/Tests/SocAlytics.Platform.Host.Tests/RegistrationContractShapeTests.cs` with the expected JSON property names of each operation's response written into the test (taken from the current anonymous objects and from `specs/archive/20261012-153308-manager-fleet-status/contracts/fleet-read-api.md`) and make it pass against the current code, then switch to the records and keep it passing; the existing registration and fleet tests pass unchanged; `OpenApiSnapshotTests` passes.

**Checkpoint**: The platform builds and its tests pass; the OpenAPI snapshot types the registration operations.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: The registration adapter, test support, and the sign-in return. No user story starts before this phase is complete.

### Adapter, test support, and sign-in return

- [x] T003 [P] Registration adapter (contracts/platform-adapter.md; data-model.md § Registration; research.md § R3, § R7; FR-014, FR-016): add `src/clients/web/src/platform/models/registrations.ts` (the client model of data-model.md, with wire values mapped and unknown values as `unknown`; derived `isOwnSubmission`, `isCurrent`, `isEnded`) and `src/clients/web/src/platform/adapters/registrationAdapter.ts` (the operations of the contract, including `listAll` following `continuationToken`); extend `src/clients/web/src/platform/failure.ts` with `pairingRefused`, `rateLimited` (with `retryAfterSeconds` from `Retry-After`), and `decisionConflict`, and the `reason` field validation, as the contract's error-mapping table defines, with keys in `src/clients/web/src/i18n/en/common.json`. Unit tests in `src/clients/web/tests/unit/platform/registrationAdapter.test.ts` for every operation, every mapping row, and unknown values.
- [x] T004 [P] Registration test support (research.md § R8): extend `src/clients/web/tests/e2e/support/simulatedApi.ts` with registration fixtures and handlers (submit with valid and refused codes, rate limiting, Registrar-scoped list and detail, Club Admin list with `status` and `connectivity`, approve, reject, revoke, conflicts) typed from the generated models; add `src/clients/web/tests/e2e/support/scriptedManager.ts` (Node `crypto`, ES256 P-256 key in memory: start pairing with `claimedKeyProtection: software` and a host label, poll the pairing status, activate with a signed challenge, obtain a DPoP-bound token with a client assertion, and send one status report through the self registration check in the request shape of `specs/archive/20261012-153308-manager-fleet-status/contracts/self-status-api.md`; every request goes to the Web UI origin, which forwards `/api`, while the proof claims (`aud`, `htu`) use the URIs from the platform's stamp discovery, which the platform checks against `Stamp:PublicBaseUri`, so the test needs no trust in the API's development certificate). Unit tests in `src/clients/web/tests/unit/support/scriptedManager.test.ts` check the proofs' headers, claims, and signatures against the golden fixtures in `contracts/analyst-manager/registration/v1/` and verify them with the public key.
- [x] T005 [P] Sign-in return (contracts/routes-and-roles.md § Sign-in return; research.md § R6; FR-002): in `src/clients/web/src/session/guards.tsx` pass the guarded route's path as `returnTo` to `/sign-in`, and in `src/clients/web/src/features/sign-in/SignInPage.tsx` navigate there after sign-in only when it "starts with exactly one `/` and matches a known route", otherwise home. Unit tests in `src/clients/web/tests/unit/session/returnTo.test.ts` (including `//`, absolute URLs, and unknown paths); a browser test in `src/clients/web/tests/e2e/simulated/sign-in-return.spec.ts`.

**Checkpoint**: The adapter is typed and tested, the scripted Manager works against the fixtures, and sign-in returns safely.

---

## Phase 3: User Story 1 - Pair a new Analyst Manager in the browser (Priority: P1) 🎯 MVP

**Goal**: The verification address opens a pairing page where Registrars and Club Admins submit the code and fingerprint.

**Independent Test**: Start a pairing with the scripted Manager, open the verification address, sign in as a Registrar, submit, and see the pending request; repeat with a wrong fingerprint and an expired code and see the same refusal.

- [x] T006 [US1] Pairing page (contracts/routes-and-roles.md; data-model.md § Pairing form; FR-001–FR-005; US1 scenarios 1–6): add `src/clients/web/src/features/managers/PairingPage.tsx` (code and fingerprint fields, "Required; trimmed; held in the form only, cleared after submission, never in an address or storage"; the result with state, host label, claimed key protection, operating system, fingerprint, and approval deadline; one generic refusal for `pairingRefused`; the rate-limit message with the wait; not-permitted for members without the Registrar or Club Admin role); register `/analyst-managers/pair` before any `/analyst-managers/:registrationId` route in `src/clients/web/src/router.tsx`; add "Pair a Manager" for Registrars and Club Admins in `src/clients/web/src/shell/Navigation.tsx`; keys in `src/clients/web/src/i18n/en/managers.json`. Unit tests in `src/clients/web/tests/unit/features/managers/pairing/`; browser tests in `src/clients/web/tests/e2e/simulated/pairing.spec.ts` for US1 scenarios 1–6, the expired and repeated code edge cases, SC-004 (identical refusals), and SC-007 (no code or fingerprint in addresses, history, or storage).
- [x] T007 [US1] Platform end-to-end pairing (SC-001, SC-003): add `src/clients/web/tests/e2e/platform/manager-fleet.spec.ts`: the Club Admin gives a new member the Registrar role through the API in test setup; the scripted Manager starts a pairing; the test opens the returned `verificationUri` (the Web UI origin), signs in as the Registrar through the return path, submits the code and fingerprint, and sees the pending request with the scripted Manager's host label.

**Checkpoint**: Pairing works against the simulated API and the real platform.

---

## Phase 4: User Story 2 - See my registration requests (Priority: P2)

**Goal**: Registrars follow their own requests.

**Independent Test**: As a Registrar with two submitted requests, see exactly those; after a rejection, see its reason; as another Registrar, see none.

- [X] T008 [US2] My registration requests (contracts/routes-and-roles.md; FR-006, FR-015; US2 scenarios 1–3): add `src/clients/web/src/features/managers/MyRequestsPage.tsx` and `src/clients/web/src/features/managers/MyRequestPage.tsx` (host label, fingerprint, state, submission and decision times, rejection reason, approval or activation deadline; no operating state or connectivity; "not found" for others' requests; refresh every 30 s while visible; Club Admins redirected to the fleet route); register `/analyst-managers/requests` and `/analyst-managers/requests/:registrationId` in `src/clients/web/src/router.tsx`; add "My registration requests" for Registrars without Club Admin in `src/clients/web/src/shell/Navigation.tsx`; extend `src/clients/web/src/i18n/en/managers.json`. Unit tests in `src/clients/web/tests/unit/features/managers/requests/`; browser tests in `src/clients/web/tests/e2e/simulated/my-requests.spec.ts` for US2 scenarios 1–3.

**Checkpoint**: User Stories 1 and 2 work independently.

---

## Phase 5: User Story 3 - Club Admins approve or reject pending requests (Priority: P3)

**Goal**: Club Admins find pending requests, approve after the fingerprint confirmation, or reject with a reason.

**Independent Test**: Open a pending request, see the warnings and the pairing origin, approve with the last 6 fingerprint characters, reject another with a reason, and let one expire.

- [x] T009 [US3] Pending requests, decision view, and pending count (data-model.md § Decision forms, § Pending count; research.md § R4, § R5; FR-007–FR-009, FR-015; US3 scenarios 1–7; clarifications on approval and the pending count): add `src/clients/web/src/features/managers/fingerprint.ts` (normalization: letters and digits only, upper-case; last 6 characters), `src/clients/web/src/features/managers/FleetPage.tsx` listing pending requests for Club Admins, `src/clients/web/src/features/managers/RegistrationPage.tsx` (every field of FR-007, the approval notice and the physical-identification warning, the additional software-key warning, the statement that protection is claimed and not verified), `src/clients/web/src/features/managers/ApproveDialog.tsx` (enabled only when the normalized input equals the last 6 normalized fingerprint characters; a mismatch message), and `src/clients/web/src/features/managers/RejectDialog.tsx` (`reason` "required, 1–500 characters"), with `decisionConflict` showing the refusal and reloading; register `/analyst-managers` and `/analyst-managers/:registrationId` for Club Admins in `src/clients/web/src/router.tsx`; add the "Analyst Managers" entry with the pending count (hidden when zero, refreshed every 60 s while signed in, invalidated after decisions) in `src/clients/web/src/shell/Navigation.tsx`; extend `src/clients/web/src/i18n/en/managers.json`. Unit tests in `src/clients/web/tests/unit/features/managers/approval/`; browser tests in `src/clients/web/tests/e2e/simulated/approval.spec.ts` for US3 scenarios 1–7 and the approval-window and concurrent-decision edge cases.
- [x] T010 [US3] Platform end-to-end approval and activation (SC-002, SC-003): extend `src/clients/web/tests/e2e/platform/manager-fleet.spec.ts`: as the Club Admin, see the pending count, open the request, enter the last 6 fingerprint characters, approve, let the scripted Manager activate, and see the registration active.

**Checkpoint**: User Stories 1–3 work independently.

---

## Phase 6: User Story 4 - Club Admins watch the fleet and revoke Managers (Priority: P4)

**Goal**: The full fleet view with status, connectivity, filters, refresh, and revocation.

**Independent Test**: With online, offline, paused, and pending Managers, compare each row; filter; revoke one with a reason and its host label; as a Registrar, reach no fleet.

- [x] T011 [US4] Fleet view and revocation (data-model.md § Fleet filter, § Decision forms; research.md § R3, § R4; FR-010–FR-014; US4 scenarios 1–6; clarifications on the default fleet, refresh, and revocation): extend `src/clients/web/src/features/managers/FleetPage.tsx` to the full fleet (host label, state, claimed key protection, connectivity with the last report time or "no report yet", operating state with the resume time in local date and time or the reason, active and maximum Analysts, version; default filter `current` with pending first, then filters for one state, `ended`, and `all` as research.md § R3 defines; own submissions marked; long host labels shortened with a tooltip; refresh every 30 s while visible) and `src/clients/web/src/features/managers/RegistrationPage.tsx` with status and connectivity; add `src/clients/web/src/features/managers/fleetFilter.ts` and `src/clients/web/src/features/managers/RevokeDialog.tsx` (host label and active Analysts, the consequences of FR-012, `reason` "required, 1–500 characters", and the confirmation "the host label (trimmed, exact) or, without a host label, the last 6 normalized fingerprint characters"); no host-local actions anywhere (FR-013); extend `src/clients/web/src/i18n/en/managers.json`. Unit tests in `src/clients/web/tests/unit/features/managers/fleet/`; browser tests in `src/clients/web/tests/e2e/simulated/fleet.spec.ts` for US4 scenarios 1–6, the never-reported, unknown-value, long-label, and platform-unreachable edge cases, SC-005 (role access), and SC-006 (a change is shown within one refresh).
- [x] T012 [US4] Platform end-to-end fleet and revocation (SC-003, SC-006): extend `src/clients/web/tests/e2e/platform/manager-fleet.spec.ts`: after the scripted Manager's status report, see it online with its operating state, active and maximum Analysts, and version in the fleet; revoke it with a reason and its host label; see it revoked under the `ended` filter.

**Checkpoint**: All user stories work independently.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [x] T015 Stabilize the concurrent-decision browser test (corrective; found by the phase 6 verification): in `src/clients/web/tests/e2e/simulated/approval.spec.ts` scenario 5 ("a request decided meanwhile shows the refusal and the current state"), `page.getByRole('alert')` is ambiguous: until the registration reloads as rejected, the decision refusal (`failure.decisionConflict`) is shown together with the info approval notice and the warning alerts of `RegistrationPage.tsx`, so Playwright's strict mode fails (3 alerts) and T011 failed attempt 2 and the phase verification on exactly this assertion. Scope the locator to the refusal (`page.getByRole('alert').filter({ hasText: 'can no longer be decided' })`) and check the other `getByRole('alert')` assertions in `src/clients/web/tests/e2e/simulated/*.spec.ts` for the same ambiguity; do not weaken the assertions or change product code. Run `npm run test:e2e -- --project simulated` in `src/clients/web/`.
- [x] T013 Documentation (plan.md § Architecture Updates; Constitution Principle I): apply every update listed in plan.md § Architecture Updates and correct any other statement in `docs/architecture/` that the merged feature makes false, including planned or future-tense wording. In particular, update `docs/architecture/analyst-manager.md` § Fleet Visibility In The Web UI (the Web UI fleet and pairing views are implemented; the claimed application profile is not shown until the headless profile exists; email notification awaits an email channel) and `docs/architecture/platform-implementation.md` (the optional verification base address for stamps whose Web UI and API origins differ in development; the fleet view in § Analyst Technology and the Analyst Manager views in § Planned Acceptance Evidence as implemented). Update `src/clients/README.md` (Analyst Manager views, the scripted Manager in the `platform` project) and the "Current State" in `AGENTS.md`.
- [x] T014 Run the validation in [quickstart.md](quickstart.md): platform restore, build, and tests, then in `src/clients/web/` `npm ci`, `npm run build`, `npm test`, and `npm run test:e2e` (both projects), and `node .github/scripts/check-markdown.mjs`; fix what fails.

---

## Dependencies & Execution Order

- **Setup (Phase 1)**: T001 and T002 in parallel.
- **Foundational (Phase 2)**: after Setup (the generated client needs T002's snapshot, and the scripted Manager the AppHost of T001); T003, T004, and T005 in parallel. Blocks all stories.
- **US1 (Phase 3)**: after Foundational. **US2 (Phase 4)**: after US1 (submitted requests). **US3 (Phase 5)**: after US1. **US4 (Phase 6)**: after US3 (it extends `FleetPage.tsx` and `RegistrationPage.tsx`).
- **Polish (Phase 7)**: after all stories; T015 (corrective test fix) comes first, then T013 and T014.
- Within each story, the platform end-to-end task comes last.

### Parallel opportunities

- Phase 1: T001 (`RegistryOptions.cs`, `StampOptions.cs`, `RegistryApiRegistration.cs`, `AppHost/Program.cs`, `Integration.Tests/Registry/VerificationBaseUriTests.cs`, `Host.Tests/PlatformHostTests.cs`) and T002 (`AnalystManagerRegistrationContracts.cs`, `AnalystManagerRegistrationEndpoints.cs`, `openapi/v1.json`, `Host.Tests/RegistrationContractShapeTests.cs`) change disjoint files.
- Phase 2: T003 (`src/platform/models/registrations.ts`, `src/platform/adapters/registrationAdapter.ts`, `src/platform/failure.ts`, `src/i18n/en/common.json`, `tests/unit/platform/registrationAdapter.test.ts`), T004 (`tests/e2e/support/simulatedApi.ts`, `tests/e2e/support/scriptedManager.ts`, `tests/unit/support/scriptedManager.test.ts`), and T005 (`src/session/guards.tsx`, `src/features/sign-in/SignInPage.tsx`, `tests/unit/session/returnTo.test.ts`, `tests/e2e/simulated/sign-in-return.spec.ts`) change disjoint files.
- No story phase has tasks with disjoint footprints: they share `router.tsx`, `Navigation.tsx`, `managers.json`, and `manager-fleet.spec.ts`.

## Implementation Strategy

1. Platform changes and the foundational Web UI pieces, then User Story 1 as the MVP (the verification address works).
2. Add User Story 2 (my requests), 3 (approval), and 4 (fleet and revocation).
3. Documentation and full validation.

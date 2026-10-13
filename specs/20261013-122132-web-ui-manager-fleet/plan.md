# Implementation Plan: Web UI Analyst Manager Fleet and Registration Approval

**Branch**: `20261013-122132-web-ui-manager-fleet` | **Date**: 2026-10-13 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261013-122132-web-ui-manager-fleet/spec.md`

## Summary

Extend the Web UI at `src/clients/web/` (Web UI Foundation) with Analyst Manager
registration and fleet views on the existing registration operations and the
fleet read API.

- **Pairing page** at `/analyst-managers/pair`, the platform's verification
  path. Registrars and Club Admins submit the pairing code and fingerprint and
  get one generic refusal for any wrong input. Signed-out visitors return to the
  page after sign-in through a validated `returnTo`.
- **My registration requests** for Registrars, read-only.
- **Fleet** for Club Admins:
  - It opens on the current registrations, pending first, with filters for one
    state, ended, or all, and refreshes every 30 s while visible.
  - It shows connectivity, operating state, Analysts, and version.
  - The detail view approves only after the last 6 fingerprint characters are
    re-entered, rejects with a reason, and revokes with a reason and the typed
    host label. A pending count in the navigation refreshes every 60 s.
- **Platform changes (minimal, no behavior change of the operations)**:
  - An optional `Registry:Pairing:VerificationBaseUri`, so that in local
    development the verification address opens the Web UI while machine
    endpoints stay on the API's HTTPS origin.
  - Typed DTOs and OpenAPI metadata for the six registration operations, so the
    generated client can use them. The OpenAPI snapshot is regenerated.
- **End-to-end tests**: a scripted Manager in the Playwright support code pairs,
  activates, reports status, and is revoked against the AppHost.

Design rationale: [research.md](research.md). Client models:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.x (strict) on Node.js 24 for the Web UI; C#
on .NET 10 for the platform changes.

**Primary Dependencies**: the foundation's pinned Web UI dependencies; no new
production or development dependency (the scripted Manager uses Node's
`crypto`). Platform: existing ASP.NET Core minimal API metadata
(`Accepts`, `Produces`).

**Storage**: none in the browser; no codes or fingerprints stored (SC-007). No
database change and no migration.

**Testing**:

- `dotnet test`:
  - options validation and the pairing response's `verificationUri`
    (Integration tests);
  - unchanged JSON of the registration operations (Host test);
  - the OpenAPI snapshot test;
  - the AppHost setting (Host test).
- `npm test`: adapter mapping, fingerprint normalization, filters and ordering,
  refresh configuration, `returnTo` validation, unknown values, and the
  scripted Manager's proofs against `contracts/analyst-manager/registration/v1/`.
- `npm run test:e2e`: the `simulated` project for every scenario, and the
  `platform` project with the scripted Manager.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux).

**Project Type**: Web application consuming the platform API, plus two small
platform changes.

**Performance Goals**:

- SC-001: pairing in under 2 minutes.
- SC-002: a decision in under 1 minute.
- SC-006: changes shown within 30 s plus the reporting interval.

**Constraints**:

- One generic pairing refusal (FR-004).
- No codes or fingerprints in addresses or storage (FR-005).
- No host-local actions (FR-013).
- Unknown values are tolerated (FR-014).
- Machine-facing URIs stay HTTPS on `Stamp:PublicBaseUri`.

**Scale/Scope**: 5 routes, 4 user stories, one adapter, two platform changes.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements `analyst-manager.md` § Browser Pairing And Approval and § Fleet Visibility In The Web UI, and `client-applications.md` § Web UI (fleet view). Two deviations are recorded in the same change: the claimed application profile is deferred until the headless profile exists (spec Assumptions), and email notification is out of scope pending an email channel. This change also updates `analyst-manager.md` (the Web UI fleet and pairing views are implemented; the profile is not shown yet) and `platform-implementation.md` (the verification base address). | Pass |
| II. Source-area ownership | Web UI code under `src/clients/web/`; platform changes in `src/platform/` (Application options, Api endpoint metadata, AppHost) with their tests. | Pass |
| III. API-first control plane | The Web UI uses only API operations; the typed DTOs make the OpenAPI document authoritative for these operations, without changing behavior. | Pass |
| IV. Evidence over claims | Unit, simulated, and platform browser tests; platform Integration and Host tests; no production readiness claimed. | Pass |
| V. Focused, minimal changes | No new dependency; the platform changes are an optional setting and endpoint metadata that the Web UI needs. | Pass |
| Tooling constraints | The merged `specs/archive/20261012-145505-web-ui-environment` and the platform environment provide Node.js, Kiota, Playwright Chromium, the .NET SDK, and Docker; nothing new is needed. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Architecture Updates

**Narratives implemented**:

- [analyst-manager.md](../../docs/architecture/analyst-manager.md) § Browser Pairing And Approval: the browser side, meaning the pairing page and the Club Admin decision view.
- [analyst-manager.md](../../docs/architecture/analyst-manager.md) § Fleet Visibility In The Web UI.
- [client-applications.md](../../docs/architecture/client-applications.md) § Web UI: the Analyst Manager fleet view.
- [platform-implementation.md](../../docs/architecture/platform-implementation.md) § API And Identity: the Analyst Manager registration operations.

**Design updates** (applied with this plan as decided, not implemented; the implementation turns them into present
tense):

- **`analyst-manager.md` § Fleet Visibility In The Web UI** (with a pointer from § Browser Pairing And Approval, whose approval view shows the claimed profile):
  - The first fleet and decision views do not show the claimed application profile; it follows once the headless profile exists and the Manager reports it.
  - Approval requires re-entering the last 6 fingerprint characters, and revocation requires typing the host label.
  - The navigation shows the pending count. Email notification of Club Admins awaits an architecture decision on an email delivery channel.
- **`platform-implementation.md` § API And Identity**: the optional verification base address (`Registry:Pairing:VerificationBaseUri`). It is for stamps whose Web UI and API origins differ, as in local development; machine-facing URIs stay on the stamp's public base address. The registration operations declare typed OpenAPI schemas.

**Implementation-status statements this feature makes true or false at merge**:

- **`platform-implementation.md` § Analyst Technology**: "The planned fleet view adds a Manager status report, … and a fleet read operation for Club Admins" must describe the fleet view as implemented in the Web UI.
- **`platform-implementation.md` § Architecture Reassessment**: "the Web UI and its offline mode … remain unsupported by implementation evidence" must name the Analyst Manager pairing, approval, and fleet views among the implemented Web UI workflows.
- **`platform-implementation.md` § Acceptance Evidence**: the evidence list gains the pairing, decision, and fleet browser tests (simulated and `platform` projects).
- **`analyst-manager.md` § Fleet Visibility In The Web UI** and **`platform-implementation.md` § API And Identity**: the design updates above, from "will" to present tense, and any remaining sentence that presents the fleet view or browser pairing as not implemented. The status report and fleet read API are made true by `specs/archive/20261012-153308-manager-fleet-status`.
- Statements that Web UI Foundation makes false (for example `client-applications.md` § Source Ownership And Delivery "The future React and TypeScript sources of the Web UI", `platform-implementation.md` § Source And Runtime Baseline "`src/clients` owns the future Web UI", and `overview.md` § Implementation Profile "React and TypeScript clients … are planned") are corrected by that feature; any that still remain when this feature merges are corrected by it.

The documentation task (T013) must make these narratives and every status statement this feature affects true for the merged state. It re-reads `docs/architecture/` at implementation time; the updates listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261013-122132-web-ui-manager-fleet/
├── plan.md
├── research.md          # Decisions R1–R8
├── data-model.md        # Registration client view, filters, forms, pending count
├── quickstart.md
├── contracts/
│   ├── routes-and-roles.md      # Routes, navigation, sign-in return
│   ├── platform-adapter.md      # Registration adapter and error mapping
│   └── platform-changes.md      # Verification base address; typed registration operations
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Application/Registry/RegistryOptions.cs, StampOptions.cs   # VerificationBaseUri
├── SocAlytics.Platform.Api/Registry/RegistryApiRegistration.cs                    # option validation
├── SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs         # typed DTO records, Accepts/Produces
├── SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationContracts.cs         # DTO records (new)
├── SocAlytics.Platform.Api/openapi/v1.json                                         # regenerated snapshot
├── SocAlytics.Platform.AppHost/Program.cs                                          # Registry__Pairing__VerificationBaseUri
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Registry/VerificationBaseUriTests.cs
    └── SocAlytics.Platform.Host.Tests/ (RegistrationContractShapeTests.cs, PlatformHostTests.cs)

src/clients/web/
├── src/platform/adapters/registrationAdapter.ts, src/platform/models/registrations.ts, src/platform/failure.ts (+ kinds)
├── src/session/ (returnTo on the signed-in guard and sign-in)
├── src/shell/Navigation.tsx (+ entries, pending count)
├── src/i18n/en/managers.json
└── src/features/managers/
    ├── PairingPage.tsx, MyRequestsPage.tsx, MyRequestPage.tsx
    ├── FleetPage.tsx, RegistrationPage.tsx, ApproveDialog.tsx, RejectDialog.tsx, RevokeDialog.tsx
    └── fingerprint.ts, fleetFilter.ts
├── tests/unit/features/managers/, tests/unit/platform/registrationAdapter.test.ts
└── tests/e2e/
    ├── simulated/pairing.spec.ts, my-requests.spec.ts, approval.spec.ts, fleet.spec.ts
    ├── platform/manager-fleet.spec.ts
    └── support/scriptedManager.ts, simulatedApi.ts (+ registration fixtures)

docs/architecture/analyst-manager.md, docs/architecture/platform-implementation.md, src/clients/README.md, AGENTS.md
```

**Structure Decision**: Extend the foundation's `src/clients/web/` with a
`features/managers/` area and one adapter. The platform changes stay in the
Registry area and the AppHost; the OpenAPI snapshot is regenerated with them.

## Complexity Tracking

No constitution violations.

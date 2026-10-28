# Implementation Plan: Club Admin Security Audit Evidence View

**Branch**: `20261025-190907-club-admin-audit-view` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-190907-club-admin-audit-view/spec.md`

## Summary

Club Admins get one read-only member operation, `GET /api/v1/audit/events` (`listSecurityAuditEvents`), over the existing append-protected `socalytics.security_audit_event` table, and an online-only Web UI area (`/audit`) that uses it. The operation lists events newest first with a keyset cursor bound to its filter combination, seven filters, a bounded page size, an allow-listed detail projection, and no count. Every read by an authenticated member is recorded as an `audit.evidence-read` event (filter shape only) in the same transaction that reads the page, so a read that cannot be audited is never served; denials and refusals are recorded independently. The only schema change is forward-only indexes for ordered, filtered reads. The Web UI adds an adapter over the regenerated Kiota client, a Club Admin–only route and navigation entry, filters kept in the address, manual refresh, and an in-memory detail dialog.

## Technical Context

**Language/Version**: C# on .NET 10 (platform, SDK pinned by `src/platform/global.json`); TypeScript on React with Vite (Web UI, Node.js 24)

**Primary Dependencies**: existing only: ASP.NET Core minimal APIs with `MapMemberApi()`, the ASP.NET Core rate limiter, Npgsql, DbUp migrations, Kiota 1.36.0 generated client, Vitest, Playwright 1.63.0, axe-based accessibility checks already used by the Web UI tests. No new packages.

**Storage**: PostgreSQL `socalytics.security_audit_event` (read-only use). One forward-only migration adds indexes. No new table, so no `PersistedTableClassifications` entry.

**Testing**: xUnit v3 integration tests (disposable PostgreSQL through Docker) in `src/platform/Tests/SocAlytics.Platform.Integration.Tests`; host OpenAPI snapshot and typed-schema tests in `SocAlytics.Platform.Host.Tests`; Vitest unit tests; Playwright `simulated` specs and a new `platform-audit` project.

**Target Platform**: Linux container runtime for tests; browser for the Web UI (Chromium in tests).

**Project Type**: web application: platform API monolith plus the React Web UI.

**Performance Goals**: first page within 2 seconds on a development stamp with 100,000 events (SC-008), backed by composite ordered indexes.

**Constraints**: read-only; no counts; response `Cache-Control: no-store`; no audit data in logs, telemetry, browser storage, or service-worker caches; every limit is a development value.

**Scale/Scope**: one operation, one migration, one Application handler with its query port, one Infrastructure query, one endpoint file, one rate-limit policy, one Web UI feature folder, one adapter, three test layers.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

| Principle | Result | Evidence |
| --- | --- | --- |
| I. Architecture Is the Design Authority | Pass | The feature implements the access-audited clause of [Security And Data Governance → Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events) and corrects statements it makes false; the `## Architecture Updates` section below names them. `GOV-AUD-001` and `POL-009` stay unresolved and Blocking. |
| II. Respect Source-Area Ownership | Pass | Code only in `src/platform/` (layers Domain-free: Application, Infrastructure, Api) and `src/clients/web/`; no new `src` child, no new project; the functional area is a folder and namespace (`IdentityAccess/Audit`), not a project. |
| III. API-First Control Plane | Pass | The Web UI reaches audit data only through the generated client and an application-owned adapter; the platform decides authorization, filtering, and minimization; no video bytes involved. The member API is described by the committed OpenAPI snapshot; no cross-component machine contract is added to `contracts/`. |
| IV. Evidence Over Claims | Pass | Integration, OpenAPI, unit, simulated, and platform browser tests cover FR-032 and FR-033; nothing is claimed as production readiness. |
| V. Focused, Minimal Changes | Pass | No new dependency, project, or abstraction beyond one query port; no change to existing events except adding the audit-read detail keys to the existing allow-list. |
| Technology and Tooling Constraints | Pass | The environment actions already provide .NET 10, Node.js 24, Kiota, Playwright, and Docker; no environment spec is needed. |

**Post-design re-check**: Pass. The design adds no violation; Complexity Tracking is empty.

## Architecture Updates

### Narratives

- [Security And Data Governance → Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events): the feature implements "access to audit evidence ... MUST itself be audited" for the read path, adds the audit-read event to the minimum event list, and keeps `GOV-AUD-001` through `GOV-AUD-010`, `DAT-009`, and `POL-009` unresolved.
- [Platform Implementation → Data Organization](../../docs/architecture/platform-implementation.md#data-organization): the audit table paragraph ("the runtime role may only insert and read them") gains the read path and its indexes.
- [Platform Implementation → API And Identity](../../docs/architecture/platform-implementation.md#api-and-identity): the paragraph on Club Admin operations "until a dedicated stamp-operator role is defined" gains the audit evidence read.
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui): the list of connected administration workflows and the narrative of the workflow operations area gain the Audit area.

### Design updates

Applied now to the owning narratives, worded as decided but not implemented; the implementation turns each into present tense:

1. `security-and-data-governance.md`, Audit Events: Club Admins read audit evidence through `GET /api/v1/audit/events` with minimization, a filter-bound cursor, and one `audit.evidence-read` event per read (development evidence; independent audit authority unresolved).
2. `platform-implementation.md`, API And Identity: the audit evidence read is a Club Admin operation until an audit-reviewer or stamp-operator role exists.
3. `client-applications.md`, Web UI: the Audit area for Club Admins (online only, memory-only data, manual refresh).

### Status statements at merge

- `security-and-data-governance.md`, Audit Events, the sentence "Access to audit evidence and every export or redaction of it MUST itself be audited" stays a requirement; the list of events that "require audit evidence" must name audit evidence reads as development evidence instead of leaving them undescribed.
- `security-and-data-governance.md`, `CTL-009`: "event and evidence implementation deferred" must say that evidence reading by Club Admins is development evidence and the production audit store, export, redaction, integrity verification, retention, and holds remain deferred.
- `platform-implementation.md`, Data Organization: "the runtime role may only insert and read them" stays true; the audit table paragraph must add that Club Admins read it through the audited operation and which indexes serve it, and "The audit table holds development audit evidence" stays.
- `platform-implementation.md`, current-state and evidence summaries that list what exists (for example the identity foundation summary around "security audit events"), `README.md`, `src/platform/README.md`, and `AGENTS.md`: the lists of current executable evidence must include the audit evidence view and its browser projects without implying production readiness.
- `client-applications.md`, Web UI: "platform configuration and administration: the Analyst Registry administration, the workflow operations area, and the agent catalog administration ... are implemented" must include the Audit area.

The documentation task MUST make these narratives, and every other status statement in `docs/architecture/` that the merged feature makes true or false, correct for the merged state, re-reading the documents at implementation time; the rewrites above are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-190907-club-admin-audit-view/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── audit-events-api.md
│   └── audit-web-ui.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Application/
│   ├── Abstractions/AuditEvent.cs                      # resource-type comment gains audit-events
│   └── IdentityAccess/Audit/                           # new folder
│       ├── ListSecurityAuditEvents.cs                  # query, handler, self-audit, validation
│       ├── ISecurityAuditQueries.cs                    # query port and view records
│       ├── AuditEvidenceOptions.cs                     # page sizes, reads per minute
│       ├── AuditDetailProjection.cs                    # detail allow-list and value rules
│       └── AuditPageCursor.cs                          # opaque cursor, filter fingerprint
├── SocAlytics.Platform.Infrastructure/
│   ├── Persistence/PostgresAuditTrail.cs               # audit-read detail keys added to the allow-list
│   ├── Persistence/IdentityAccess/SecurityAuditQueries.cs
│   └── Persistence/Migrations/0033_identityaccess_audit_read_indexes.sql
├── SocAlytics.Platform.Api/
│   ├── Endpoints/IdentityAccess/AuditEvidenceEndpoints.cs
│   ├── Endpoints/IdentityAccess/AuditEvidenceRateLimiting.cs
│   ├── Program.cs                                      # registrations
│   └── appsettings.Development.json                    # AuditEvidence development values
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/IdentityAccess/AuditEvidence*Tests.cs
    └── SocAlytics.Platform.Host.Tests/                 # OpenAPI snapshot refresh, AuditEvidenceOpenApiTests

src/clients/web/
├── src/features/audit/                                 # list page, filters, detail dialog, formatting
├── src/platform/adapters/auditAdapter.ts
├── src/platform/models/                                # audit view models
├── src/router.tsx, src/shell/                          # route and Club Admin navigation entry
├── src/i18n/                                           # translations
├── src/generated/platform-api/                         # regenerated Kiota client
├── playwright.config.ts                                # project platform-audit
└── tests/{unit,e2e/simulated,e2e/platform}/            # audit tests
```

**Structure Decision**: follow the existing layering and the workflow operations precedent: the Application handler owns authorization, validation, the self-audit, and projection; Infrastructure owns the keyset SQL; Api owns the endpoint, DTOs, rate limiting, and headers; the Web UI mirrors `features/operations` with an adapter over the generated client.

## Complexity Tracking

No constitution violations.

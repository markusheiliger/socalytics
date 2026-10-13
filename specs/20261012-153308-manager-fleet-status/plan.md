# Implementation Plan: Analyst Manager Status Report and Fleet Read API

**Branch**: `20261012-153308-manager-fleet-status` | **Date**: 2026-10-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261012-153308-manager-fleet-status/spec.md`

## Summary

- **Platform**:
  - A new Manager operation `POST /api/v1/analyst-managers/self/status`
    carries the status report and answers with the same registration
    information as the existing `GET …/self/registration`, plus
    `statusCheckIntervalSeconds`. The Manager already reads that field but the
    platform never sent it.
  - The report is stored as the single latest row per registration in
    `registry.analyst_manager_status` (no history), and is deleted in the unit
    of work that revokes or completes the unregistration of the registration.
  - The existing registration list and detail gain `status` and `connectivity`
    for Club Admins. Registrars without the Club Admin role get only the
    registrations they submitted, without status or connectivity.
  - `DeviceMetadata` also refuses control characters in the host label.
- **Analyst Manager**:
  - `RegistrationStatusMonitor` sends the report with every check: the
    operating state and pause end from `OperatingStateMachine`, the runtime
    reason from the latest preflight, active attempts and the effective limit
    from the attempt registry and concurrency limit of Analyst Manager Work
    Execution, and the version.
  - It uses the interval the platform returns.
  - The CLI's existing `--host-label` is validated locally (1–64 characters,
    no control characters).

Design rationale: [research.md](research.md). Entities:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 for both parts (same SDK pin).

**Primary Dependencies**:

- Existing platform: minimal APIs, Dapper and PostgreSQL, the unit of work,
  `RequireAnalystManager` (DPoP), `IAccessAuthorizer` with
  `ClubPermission.Administer` and `ClubPermission.Register`, `IAuditTrail`, and
  the registration store and handlers.
- Existing Manager: `PlatformClient`, `RegistrationStatusMonitor`,
  `OperatingStateMachine`, `PreflightScheduler`, and, from Analyst Manager Work
  Execution, `AttemptRegistry` and `ConcurrencyLimit`.
- No new packages.

**Storage**: Migration `NNNN_registry_analyst_manager_status.sql` (the next
free number at implementation time; `0016` at planning time after `0014` Work
Execution and `0015` Registry). It creates one row per registration and no
`version` column, so the table is classified in
`PersistedTableClassifications.cs`.

**Testing**: Platform integration tests (PostgreSQL) with the real DPoP path
(`RegisteredAnalystManagerClient`) and the test Manager client. Manager tests
with `FakePlatform` and fake time. Shared golden fixtures for the status
request and response in `contracts/analyst-manager/status/v1/`.

**Target Platform**: Platform on Linux containers; Manager on Windows, macOS,
and Linux.

**Project Type**: Web service plus desktop worker.

**Performance Goals**: One small upsert per Manager per interval (default
60 s); the fleet list is paged like the existing registration list (50 per
page).

**Constraints**:

- No report history (FR-002, FR-012).
- Platform receipt time only (edge case: clock skew).
- An invalid report never blocks the registration answer (FR-003).
- Registrars see only their own requests (FR-007).

**Scale/Scope**: Tens of Managers per club.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements `analyst-manager.md` § Fleet Visibility In The Web UI exactly: the report rides with the existing registration check, latest only, offline after missed intervals, Club Admins see all, Registrars their own requests, no remote actions. The narrative's sentence "The status report and the fleet read operation are not yet implemented" is updated in the same change. | Pass |
| II. Source-area ownership | Platform in `src/platform`, Manager in `src/analysts/manager`, shared fixtures in `contracts/analyst-manager/`. | Pass |
| III. API-first control plane | The report and the fleet read are REST operations; the fleet read never addresses a Manager. | Pass |
| IV. Evidence over claims | Integration, Manager, and golden-fixture tests; no production values claimed. | Pass |
| V. Focused, minimal changes | Extends the existing self endpoints and registration list instead of adding parallel ones; no UI. | Pass |
| Tooling constraints | .NET and Docker only; no environment feature needed. Depends on Analyst Manager Work Execution for active and maximum Analysts. | Pass |

Post-design re-check: passes.

## Project Structure

### Documentation (this feature)

```text
specs/20261012-153308-manager-fleet-status/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── self-status-api.md      # POST /api/v1/analyst-managers/self/status
│   └── fleet-read-api.md       # Registration list and detail extensions, Registrar view
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/analyst-manager/status/v1/                # golden request and response fixtures (both solutions)

src/platform/
├── SocAlytics.Platform.Domain/Registry/ManagerStatusReport.cs     # validation (states, numbers, lengths)
├── SocAlytics.Platform.Domain/Registry/DeviceMetadata.cs          # + control-character refusal for hostLabel
├── SocAlytics.Platform.Application/Registry/MachineAccess/ReportStatus.cs   # report + own registration + interval
├── SocAlytics.Platform.Application/Registry/StatusReporting/      # options, IManagerStatusStore, connectivity derivation
├── SocAlytics.Platform.Application/Registry/Decisions/ReadRegistrations.cs  # + Registrar path, status and connectivity
├── SocAlytics.Platform.Application/Registry/Decisions/RevokeRegistration.cs # + delete the report
├── SocAlytics.Platform.Application/Registry/CompleteUnregistration.cs       # + delete the report
├── SocAlytics.Platform.Infrastructure/Persistence/Registry/ManagerStatusStore.cs
├── SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_registry_analyst_manager_status.sql
├── SocAlytics.Platform.Api/Registry/AnalystManagerSelfEndpoints.cs          # + POST self/status
├── SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs  # + status and connectivity in the DTO
└── Tests/…Integration.Tests/Registry/ and Contracts.Tests/

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/Registration/RegistrationStatusMonitor.cs   # send the report, use the interval
├── SocAlytics.Analysts.Manager.Core/Platform/PlatformClient.cs                  # + BuildStatusReport
├── SocAlytics.Analysts.Manager.Core/Diagnostics/StatusReportBuilder.cs          # collects state, attempts, limit, version
├── SocAlytics.Analysts.Manager/Cli/ManagerCli.cs                                # validate --host-label
└── Tests/SocAlytics.Analysts.Manager.Tests/

docs/architecture/analyst-manager.md      # remove "not yet implemented" for the report and fleet read
src/platform/README.md, src/analysts/manager/README.md, AGENTS.md
```

**Structure Decision**: Extend the existing Registry functional area and the
Manager's registration monitor; no new projects.

## Complexity Tracking

No constitution violations.

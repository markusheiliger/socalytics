# Implementation Plan: Analyst Manager Minimum Version

**Branch**: `20261017-213549-manager-minimum-version` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261017-213549-manager-minimum-version/spec.md`

## Summary

The stamp's deployment configuration gains `Registry:ManagerVersion:Minimum` (empty = no minimum, validated at start,
reloadable). The platform returns the current minimum in the response of the Manager's periodic status report and
refuses queue-credential issuance and new job claims from Managers whose effective version (latest status report, or
the pairing claim) is below it, with the `403` problem `analyst-manager-version-below-minimum`, a structured log, and a
counter; running attempts and every other Manager operation are untouched. The Manager compares the minimum with its
single build version, stops admitting work and requesting queue credentials while below it, and shows an update notice
in the tray status window, a tray menu line, and the local-control CLI `status`. The Club Admin fleet read and the
Web UI fleet view mark Managers below the minimum. Technical decisions are in [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (platform and Analyst Manager); TypeScript with React for the Web UI

**Primary Dependencies**: ASP.NET Core minimal APIs, `Microsoft.Extensions.Options` (`IOptionsMonitor`),
System.Text.Json, Avalonia (tray), System.CommandLine (CLI), the generated Kiota client, react-i18next; no new package

**Storage**: No change; reads the existing latest status report (`manager_version`) and the registration's device
metadata

**Testing**: xUnit v3 with Shouldly in the platform Host, Integration, Contracts, and EndToEnd tests and in the Manager
tests (Avalonia headless for the tray); Vitest and Playwright `simulated` for the Web UI

**Target Platform**: Platform API in containers through the Aspire AppHost; Manager tray on Windows, macOS, and Linux;
headless profile on Linux; Web UI on Chromium-based browsers

**Project Type**: Layered web service, desktop and headless agent, and web client in one monorepo

**Performance Goals**: The gate adds at most one indexed read per credential request and per new claim; a changed
minimum reaches each Manager within one registration-check interval plus 10 seconds (SC-003)

**Constraints**: Additive contract changes only within `/api/v1` and the v1 schemas; no refusal of operations on work
already held; no production values for the minimum; no credentials or tokens in logs

**Scale/Scope**: One configuration value, two refusal points, one status-response property, two fleet properties,
three Manager status surfaces

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements [Updates](../../../docs/architecture/analyst-manager.md#updates) (minimum version only) and [Fleet Visibility In The Web UI](../../../docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui); `## Architecture Updates` lists narratives, design updates, and status statements |
| II. Source-area ownership | Pass | Platform code in `src/platform/`, Manager code in `src/analysts/manager/`, Web UI in `src/clients/web/`, contracts in `contracts/`; no new first-level area |
| III. API-first control plane | Pass | The minimum reaches Managers and the Web UI only through `/api/v1` operations; the platform enforces it server-side; contracts stay authoritative (status schema `1.1.0`, OpenAPI snapshot) |
| IV. Evidence over claims | Pass | Host, Integration, Contracts, EndToEnd, Manager, Vitest, and Playwright tests cover every requirement; no production-readiness claim |
| V. Focused, minimal changes | Pass | No table, migration, package, or project; one options class, one gate, additive properties |
| Tooling | Pass | The environment actions already provide .NET 10, Docker, SoftHSM2, Node 24, Kiota, and Playwright Chromium; no environment spec is needed |

Post-design re-check: unchanged, all gates pass.

## Architecture Updates

### Narratives

- [Analyst Manager → Updates](../../../docs/architecture/analyst-manager.md#updates): the minimum-version bullet and the
  manual-updates sentence ("the minimum version still applies") become implemented; the feed, channels, pinning,
  attestation, and self-update stay planned.
- [Analyst Manager → Tray Profile](../../../docs/architecture/analyst-manager.md#tray-profile) and
  [Headless Server Profile](../../../docs/architecture/analyst-manager.md#headless-server-profile): the status window, the
  tray menu, and the CLI `status` show the update notice.
- [Analyst Manager → Fleet Visibility In The Web UI](../../../docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui):
  the fleet view marks Managers below the minimum.
- [Analyst Manager → Machine Authentication](../../../docs/architecture/analyst-manager.md#machine-authentication): queue
  credentials and claims are additionally refused below the minimum.
- [Platform Implementation → Analyst Technology](../../../docs/architecture/platform-implementation.md#analyst-technology):
  the paragraph on installers, updates, and the fleet view.
- [Analysts, Models, and Hardware → Implementation Validation](../../../docs/architecture/analysts-models-and-hardware.md#implementation-validation):
  the Velopack spike list names the minimum version.
- [Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility) is
  followed (additive v1 changes) but not changed.

### Design updates applied now

Worded as decided, not implemented; the implementation turns them into present tense.

- `docs/architecture/analyst-manager.md` → Updates: a paragraph after the bullet list states the decided mechanism:
  the minimum is the deployment setting `Registry:ManagerVersion:Minimum` (empty means none), it reaches each Manager
  in the response of its periodic status report, the Manager compares it with the build version it reports, and the
  platform independently refuses queue credentials and new claims below it with the problem
  `analyst-manager-version-below-minimum`, while operations on work already held continue; it applies before the
  update feed exists. Marked as decided and not yet implemented.
- `docs/architecture/analyst-manager.md` → Fleet Visibility In The Web UI: the per-Manager field list adds whether the
  version is below the stamp's minimum, marked as decided and not yet implemented.

### Status statements at merge

- `analyst-manager.md` → Updates, the new paragraph: "The minimum version is decided ahead of the update feed and not
  yet implemented" → present tense, implemented.
- `analyst-manager.md` → Fleet Visibility In The Web UI: "Decided but not yet implemented: the view also marks every
  Manager … below the stamp's minimum" → implemented; the paragraph "The Web UI fleet, pairing, and decision views are
  implemented …" also names the minimum-version marking.
- `analyst-manager.md` → Tray Profile status-window list and Headless Server Profile: add the update notice as shown.
- `analyst-manager.md` → Technology And Delivery: "Installers and updates are open promotion items" stays true; it must
  not imply that the minimum version is unimplemented.
- `platform-implementation.md` → Analyst Technology: "Each stamp serves the update feed … and declares a minimum
  Manager version" → the minimum version is implemented through deployment configuration; the feed, channels, and
  pinning remain planned after the Velopack spike.
- `analysts-models-and-hardware.md` → Implementation Validation: "the stamp-served update feed with attestation checks,
  minimum version, and drain before applying" → the spike proves the feed with attestation checks and drain; the
  minimum version is already enforced by the platform and the Manager.
- Any other statement found at implementation time that says the Manager has no version enforcement.

The documentation task MUST make the listed narratives and every status statement this feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time; the rewrites above are examples, not the whole
job. It also updates `src/platform/README.md`, `src/analysts/manager/README.md`, and `contracts/README.md` (status
schema `1.1.0`).

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261017-213549-manager-minimum-version/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── manager-minimum-version.md
├── checklists/
│   └── requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/
├── README.md                                                    # status schema 1.1.0
└── analyst-manager/status/v1/
    ├── status.schema.json                                       # + minimumManagerVersion (1.1.0)
    ├── releases/1.0.0.schema.json                               # new immutable copy
    ├── minimum-version.fixtures.json                            # new comparison cases
    └── examples/{valid,invalid}/…minimum….response.json         # new examples

src/platform/
├── README.md
├── SocAlytics.Platform.Domain/Registry/ManagerVersion.cs        # new value: parse and compare
├── SocAlytics.Platform.Application/
│   ├── Abstractions/OperationFailure.cs                         # optional Extensions
│   ├── ApplicationServiceCollectionExtensions.cs                # options, provider, gate
│   ├── Registry/ManagerVersions/                                # new: ManagerVersionOptions, MinimumManagerVersionProvider, ManagerVersionGate
│   ├── Registry/StatusReporting/IManagerStatusStore.cs          # + FindVersionAsync
│   ├── Registry/MachineAccess/ReportStatus.cs                   # + MinimumManagerVersion in result
│   ├── Registry/QueueCredentials/IssueQueueCredentials.cs       # gate
│   ├── Registry/Decisions/ReadRegistrations.cs                  # fleet flags
│   ├── Analysis/Commands/ClaimLogicalJob.cs                     # gate on new attempts
│   └── Telemetry/AnalysisMetrics.cs                             # refusal counter
├── SocAlytics.Platform.Infrastructure/Persistence/Registry/ManagerStatusStore.cs   # FindVersionAsync
├── SocAlytics.Platform.Api/
│   ├── Http/ProblemResults.cs                                   # extension members, title, detail
│   ├── Registry/AnalystManagerRegistrationContracts.cs          # fleet DTO properties
│   ├── Registry/AnalystManagerRegistrationEndpoints.cs          # map flags
│   ├── Registry/AnalystManagerSelfEndpoints.cs                  # status response carries the minimum
│   └── openapi/v1.json                                          # refreshed snapshot
└── Tests/
    ├── SocAlytics.Platform.Host.Tests/ManagerVersionOptionsTests.cs
    ├── SocAlytics.Platform.Contracts.Tests/StatusReportContractTests.cs
    ├── SocAlytics.Platform.Integration.Tests/Registry/          # ManagerVersionTests, StatusMinimumVersionTests,
    │                                                            # MinimumVersionEnforcementTests, FleetMinimumVersionTests
    └── SocAlytics.Platform.EndToEnd.Tests/MinimumVersionEndToEndTests.cs

src/analysts/manager/
├── README.md
├── contracts/local-control.schema.json                          # + status.update
├── SocAlytics.Analysts.Manager.Core/
│   ├── SocAlytics.Analysts.Manager.Core.csproj                  # <Version>0.1.0</Version>
│   ├── Diagnostics/ManagerBuildVersion.cs                       # new single version source
│   ├── Diagnostics/StatusReportBuilder.cs, ManagerStatusSnapshot.cs
│   ├── Registration/ManagerVersionComparer.cs, MinimumVersionState.cs   # new
│   ├── Registration/RegistrationCoordinator.cs, RegistrationStatusMonitor.cs
│   ├── Platform/PlatformContracts.cs, PlatformClient.cs
│   ├── Operating/AdmissionGate.cs
│   ├── Queue/QueueCredentialProvider.cs
│   ├── Work/AttemptRunner.cs
│   ├── Hosting/ManagerServiceCollectionExtensions.cs, ManagerControl.cs
│   ├── LocalControl/LocalControlServer.cs
│   └── Cli/ManagerCli.cs
├── SocAlytics.Analysts.Manager/
│   ├── ViewModels/StatusWindowViewModel.cs, Views/StatusWindow.axaml
│   └── Tray/TrayMenuBuilder.cs
└── Tests/SocAlytics.Analysts.Manager.Tests/
    ├── Registration/ManagerVersionComparerTests.cs, MinimumVersionStateTests.cs
    ├── Work/MinimumVersionAdmissionTests.cs, MinimumVersionRefusalTests.cs
    ├── LocalControl/StatusMinimumVersionTests.cs
    └── Ui/StatusWindowMinimumVersionTests.cs

src/clients/web/
├── src/platform/models/registrations.ts, src/platform/adapters/registrationAdapter.ts
├── src/features/managers/StatusLines.tsx, FleetPage.tsx, RegistrationPage.tsx
├── src/i18n/en/managers.json
└── tests/
    ├── unit/platform/registrationAdapter.test.ts, unit/features/managers/fleet/fleet.test.tsx
    └── e2e/simulated/fleet.spec.ts, e2e/support/simulatedApi.ts
```

**Structure Decision**: The layered platform monolith (Domain, Application, Infrastructure, Api) keeps the feature in
its Registry and Analysis folders; the Analyst Manager keeps it in Core with thin tray and CLI surfaces; the Web UI
adds display only. No new project or first-level directory.

## Complexity Tracking

No constitution violations.

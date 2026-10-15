# Implementation Plan: Analyst Manager Headless Server Profile

**Branch**: `20261015-172427-manager-headless-profile` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261015-172427-manager-headless-profile/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

A new console executable `socalytics-manager-server` hosts the shared Core
worker without Avalonia for Linux servers. It reuses the CLI, which moves from
the tray project into Core, so both profiles offer identical commands. In the
headless profile, `autostart on|off` installs and enables a systemd user unit
instead of the XDG desktop entry, a start-time check warns when lingering is
off, and preflight adds an account rule: a rootful runtime reached through
group membership under a login-capable account makes the runtime unavailable.
Managers claim their application profile at pairing in an optional, additive
`deviceMetadata` field that the platform validates and stores with the other
claimed metadata (no migration). The boolean `AllowSoftwareKeys` becomes a
three-value software key policy (`refused`, `headless-server-only`, `all`,
the last only in Development and Test), which stamp discovery publishes per
profile. The Web UI shows the claimed profile next to the claimed key
protection. Real systemd behavior on Ubuntu Server 22.04 and 24.04 LTS is a
manual validation.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/analysts/manager/global.json`,
`src/platform/global.json`); TypeScript with React 19 in `src/clients/web`.

**Primary Dependencies**: Existing Manager Core (Generic Host, local control,
`System.CommandLine`, PKCS#11 key provider), platform minimal APIs and
options validation, Web UI MUI and the Kiota client. No new packages.

**Storage**: PostgreSQL; no migration (`device_metadata` jsonb gains a field).
Manager: one user unit file under `$XDG_CONFIG_HOME/systemd/user/`.

**Testing**: Manager xUnit v3 tests (server executable, CLI parity, lingering,
unit text and registrar with a fake process runner, account rule with the
simulated adapter, pairing claim, golden fixtures); platform Integration tests
(policy, validation, discovery, audit, reads), Host tests (start-up
validation, OpenAPI snapshot), Contracts tests (fixtures); Web UI Vitest and
simulated Playwright tests; manual validation on Ubuntu Server 22.04 and
24.04 LTS ([quickstart.md](quickstart.md)).

**Target Platform**: Linux servers (Ubuntu Server 22.04 and 24.04 LTS) for the
headless executable; the platform API and Web UI as today.

**Project Type**: Desktop/server agent plus web service and web application
(existing).

**Performance Goals**: SC-001: running within 60 seconds after boot.

**Constraints**: No Avalonia in the headless executable; no administrator
rights and no system-wide unit; v1 contracts change only additively; `all`
software key policy refused outside Development and Test.

**Scale/Scope**: One Manager instance per account; a handful of Managers per
club.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements the headless profile, the claimed profile, and the software key exception of `analyst-manager.md`, the account rule of `analyst-runtime-and-recovery.md`, and the headless validation of `analysts-models-and-hardware.md`; the additive claim and the three-value policy reach the architecture through `## Architecture Updates` and the documentation task. |
| II. Source-area ownership | Pass | Manager code in `src/analysts/manager` (a new project with its first artifacts), platform code in the Registry area of `src/platform`, Web UI code in `src/clients/web`, fixtures in `contracts/`. |
| III. API-first control plane | Pass | The claim, the policy, and discovery are versioned API changes in the OpenAPI snapshot and the registration contracts; the Web UI uses the generated client. |
| IV. Evidence over claims | Pass | Automated tests for every requirement that CI can exercise; the boot behavior is a documented manual validation, not a production-readiness claim. |
| V. Focused, minimal changes | Pass | One new project justified by "Core worker without Avalonia" (research.md § R1); no migration; no new packages. |
| Tooling | Pass | .NET 10, SoftHSM2, Docker, Kiota, and Playwright are provided by the environment actions; no environment feature is needed (research.md § R11). |

Re-check after Phase 1 design: still passes; no Complexity Tracking entries.

## Architecture Updates

### Narratives

- [Analyst Manager](../../docs/architecture/analyst-manager.md): Application
  Profiles, Headless Server Profile, Browser Pairing And Approval (claimed
  profile, software-backed key exception), Fleet Visibility In The Web UI
  (claimed profile) — implemented by this feature.
- [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md):
  the account rule for rootful runtimes in the Linux profiles (the rule is
  implemented and exercised with the simulated runtime; Linux runtime adapters
  stay future).
- [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md):
  Implementation Validation (the headless profile on Ubuntu Server 22.04 and
  24.04 LTS with the lingering check and the account rule; rootless and rootful
  runtimes stay open until a Linux runtime adapter exists).
- [Platform Implementation](../../docs/architecture/platform-implementation.md):
  Analyst Technology (the headless server profile) and Architecture
  Reassessment (the Avalonia/OCI platform matrix gains headless evidence).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md):
  `GOV-CRED-002` (the stamp exception for software-backed keys on headless
  Linux servers without a TPM is enforced; production approval of key stores
  stays unresolved).

### Design updates

Applied now, worded "decided, not yet implemented":

1. **Headless executable and controls** (`analyst-manager.md` Headless Server
   Profile): a separate console executable `socalytics-manager-server` over
   the Core worker without Avalonia, one shared CLI, `autostart on|off`
   managing the systemd user unit, the lingering marker check that only warns,
   the account rule as a failing preflight check (runtime-unavailable, no work
   admitted, still registered), and refusal on Windows and macOS.
2. **Claimed profile and software key policy** (`analyst-manager.md` Browser
   Pairing And Approval): an optional, additive `claimedApplicationProfile` in
   the v1 device metadata, validated, immutable, missing claims shown as not
   reported and treated like `tray`; the three-value software key policy with
   `all` only in development and test; discovery lists the accepted profiles.
3. **Account rule behavior** (`analyst-runtime-and-recovery.md`): a
   login-capable account with a rootful runtime makes the headless Manager
   runtime-unavailable and admits no work.

The implementation turns updates 1 to 3 into present tense and drops their
"decided, not yet implemented" markers.

### Status statements at merge

- `analyst-manager.md`, Fleet Visibility In The Web UI: "They do not show the
  claimed application profile: it follows once the headless server profile
  exists and Managers report it" → the views show the claimed profile, with
  "not reported" for older registrations.
- `analyst-manager.md`, Browser Pairing And Approval: "(the Web UI decision
  view shows the profile only once Managers report it; …)" → Managers report it
  and the decision view shows it.
- `platform-implementation.md`, Analyst Technology: "in the planned headless
  server profile, the worker runs without Avalonia as a Linux systemd user
  service" → the headless server profile exists as the separate executable.
- `platform-implementation.md`, Architecture Reassessment: "the
  Avalonia/OCI platform matrix remain unsupported by implementation evidence"
  → keep the matrix open, but record the automated headless evidence and list
  the manual Ubuntu Server 22.04 and 24.04 LTS validation as open (performed by
  a person after the merge), without claiming the full matrix.
- `analysts-models-and-hardware.md`, Implementation Validation: record which
  headless validations now have automated evidence (lingering check, account
  rule with the simulated runtime) and which stay open (the Ubuntu Server boot
  behavior until a person performs the manual validation, and rootless and
  rootful runtimes with a real Linux adapter).
- `security-and-data-governance.md`, `GOV-CRED-002`: record that the
  headless-only software key exception is enforced; production values and
  store approval stay unresolved.
- Outside `docs/architecture/` (kept current by the documentation task):
  `src/analysts/manager/README.md` ("Planned, not implemented: a headless
  server profile for Linux …", Controls, Autostart, Device Key Providers),
  `src/platform/README.md` (`Registry` option defaults: `AllowSoftwareKeys`),
  `AGENTS.md` Current State (the Manager's executables), and `README.md` where
  it lists Manager commands.

The documentation task in `tasks.md` must make every narrative above and every
status statement the feature affects anywhere in `docs/architecture/` true for
the merged state, re-reading the documents at implementation time; the
rewrites listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/20261015-172427-manager-headless-profile/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── headless-manager.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
src/analysts/manager/
├── SocAlytics.Analysts.Manager.slnx                     # + Server project
├── SocAlytics.Analysts.Manager.Server/                  # new console executable socalytics-manager-server
│   ├── SocAlytics.Analysts.Manager.Server.csproj        # references Core only
│   └── Program.cs                                       # Linux check, commands, single instance, host, SIGTERM
├── SocAlytics.Analysts.Manager/
│   ├── SocAlytics.Analysts.Manager.csproj               # System.CommandLine moves to Core
│   └── Program.cs                                       # host built with ApplicationProfile.Tray
├── SocAlytics.Analysts.Manager.Core/
│   ├── Cli/ManagerCli.cs                                # moved from the tray project; profile and lingering lines
│   ├── Hosting/ApplicationProfile.cs, ManagerHost.cs    # profile passed by the executable
│   ├── Hosting/LingeringCheck.cs                        # marker file check
│   ├── Autostart/SystemdUserUnitRegistrar.cs, AutostartEntryFormats.cs   # unit text and systemctl
│   ├── Runtime/IContainerRuntimeAdapter.cs, PreflightRunner.cs           # DaemonAccess, account-isolation
│   ├── Runtime/AccountShell.cs                          # getpwuid_r login shell lookup
│   ├── Runtime/Simulated/SimulatedAnalystOptions.cs, SimulatedRuntimeAdapter.cs  # DaemonAccess option
│   ├── Registration/RegistrationCoordinator.cs, StampDiscovery*.cs       # claim, softwareKeysAllowedFor
│   └── LocalControl/…                                   # status: applicationProfile, lingering
├── contracts/local-control.schema.json                  # additive status fields
├── README.md                                            # Headless Server Profile section
└── Tests/SocAlytics.Analysts.Manager.Tests/             # Server/, Cli/, Autostart/, Runtime/, Registration/, GoldenFixtures/

src/platform/
├── SocAlytics.Platform.Domain/Registry/DeviceMetadata.cs            # ClaimedApplicationProfile and validation
├── SocAlytics.Platform.Application/Registry/RegistryOptions.cs      # SoftwareKeyPolicy
├── SocAlytics.Platform.Application/Registry/Pairing/StartPairing.cs # policy per profile, audit details
├── SocAlytics.Platform.Application/Registry/RegistryAuditEvents.cs
├── SocAlytics.Platform.Api/Registry/AnalystManagerPairingEndpoints.cs  # softwareKeysAllowedFor
├── SocAlytics.Platform.Api/Registry/RegistryApiRegistration.cs      # start-up validation of `all`
├── SocAlytics.Platform.Api/Registry/AnalystManagerRegistrationEndpoints.cs  # read DTOs
├── SocAlytics.Platform.Api/openapi/v1.json
├── SocAlytics.Platform.AppHost/Program.cs                           # Registry__SoftwareKeys=all
├── README.md                                                        # Registry option defaults
└── Tests/                                                           # Integration (Registry), Host, Contracts, EndToEnd support

contracts/analyst-manager/registration/v1/
├── pairing.request.json, stamp-discovery.response.json             # additive fields
└── negative/                                                        # unknown and non-Linux headless claims

src/clients/web/
├── src/features/managers/PairingPage.tsx, MyRequestPage.tsx, RegistrationPage.tsx, FleetPage.tsx
├── src/platform/adapters/…                                          # profile mapping
└── tests/unit/…, tests/e2e/simulated/…, tests/e2e/support/simulatedApi.ts
```

**Structure Decision**: The headless profile is a second executable in the
Manager solution next to the tray executable, both thin hosts over Core; the
platform change stays in the Registry area of each layer; the Web UI extends
the existing Manager views through its adapters.

## Complexity Tracking

No constitution violations.

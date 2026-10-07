# Implementation Plan: Analyst Manager Registration

**Branch**: `20261005-130704-analyst-manager-registration` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261005-130704-analyst-manager-registration/spec.md`

## Summary

Deliver the first testable Analyst Manager slice in two parts that meet only at
the versioned REST contract and the shared golden fixtures.

- **Platform (`src/platform`, Registry functional area)**: device-code-style
  pairing (high-entropy single-use pairing code and polling handle, hash-only
  storage, indistinguishable refusals, rate limiting) hardened against
  device-code phishing by a device fingerprint that the Registrar enters with
  the code and by the pairing origin and an approval notice in every decision
  view; key protection recorded only as claimed, with software-backed keys
  refused unless `Registry:AllowSoftwareKeys` is set; Registrar submission and
  Club Admin approve/reject/revoke as separately authorized, state-guarded,
  audited lifecycle commands, activation by a signed single-use platform
  challenge, durable registration persistence with the full lifecycle state
  machine, and a minimal in-API OAuth 2.0 token endpoint (`client_credentials` +
  `private_key_jwt` + mandatory DPoP bound to the device key) that issues
  short-lived opaque access tokens validated against the authoritative
  registration and stamp binding on every Manager request.
- **Analyst Manager (`src/analysts/manager`)**: a per-user Avalonia 12.1.3 tray
  application for Windows, macOS, and Linux (no service, no administrator
  rights) hosting an in-process, UI-free .NET 10 Generic Host worker. The tray
  menu offers status, pause, timed pause, resume, safe exit, register,
  unregister, and a start-at-sign-in toggle, with a status window where no tray
  host exists. Device keys come from per-OS providers behind
  `IDeviceKeyProvider` (Windows CNG TPM-first, macOS Secure Enclave via a Swift
  bridge, Linux PKCS#11 with tpm2-pkcs11); local state is an owner-only file
  signed with the device key. The worker restores and re-proves its identity,
  confirms it with the platform, observes revocation, drains safely, runs a
  container-runtime preflight against a built-in compatibility list (validated
  with a simulated runtime), and registers per-user autostart. One owner-only
  Unix socket provides single-instance hand-off and an optional scripting CLI.
- **Shared golden fixtures** under repository-root
  `contracts/analyst-manager/registration/v1/` are verified by both solutions
  (FR-039).

Design rationale: [research.md](research.md). Entities and state machines:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 for both parts; SDK 10.0.400
(`rollForward: latestPatch`) from `src/platform/global.json`, pinned identically
in `src/analysts/manager/global.json`; nullable enabled, warnings as errors.
Swift 5.9+ (Xcode command-line tools) for the macOS bridge library only.

**Primary Dependencies**:

- Platform: ASP.NET Core minimal APIs, built-in OpenAPI, in-box
  `Microsoft.AspNetCore.RateLimiting`; from Platform Persistence Foundation
  Npgsql, Dapper, DbUp, `IUnitOfWork.BeginAsync` → `IUnitOfWorkScope`,
  `socalytics.attach_version_trigger(regclass)`, and
  `Structure/PersistedTableClassifications.cs`; from Club and Identity
  Foundation the `SocAlyticsSession` scheme (cookie
  `__Host-socalytics-session`), `ActiveMember` policy, `MapMemberApi`,
  `SessionAntiforgeryFilter` (`X-CSRF-Token`), `ProblemResults`,
  `OperationResult<T>`/`OperationFailure`, `IRequestContext`,
  `IAccessAuthorizer`/`ClubPermission`, `IAuditTrail`/`AuditEvent`, and the
  `member_account` and `club` tables; from Recording Lineage and Upload (merged
  before this feature) the `contracts/` index, `SocAlytics.Platform.Contracts.Tests`
  with `ContractCatalog` (`Schemas`, `IndexRows`, `LoadRegistry()`),
  `ContractIndexTests`, `SchemaMetaValidationTests`, and `JsonSchema.Net` 8.0.5,
  which this feature only extends. New
  package `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 (Infrastructure) for
  JWS validation and RFC 7638 thumbprints, added only if absent. No OpenIddict or Duende (research R1).
- Manager Core: `Microsoft.Extensions.Hosting` 10.0.12, `Microsoft.Extensions.Http`
  10.0.12, `Microsoft.IdentityModel.JsonWebTokens` 8.23.0, `Pkcs11Interop` 5.3.0;
  in-box Windows CNG, `Microsoft.Win32.Registry`, and `UnixDomainSocketEndPoint`.
- Manager app: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` 12.1.3,
  `System.CommandLine` 2.0.12 (optional CLI).
- macOS bridge: CryptoKit, Security, and ServiceManagement frameworks.

**Storage**:

- Platform: stamp PostgreSQL schema `socalytics`, three new tables
  (`analyst_manager_registration` versioned root, `analyst_manager_access_token`
  and `analyst_manager_proof_replay` unversioned) in one migration described as
  "registry analyst manager registrations", which also extends the
  `security_audit_event` value checks; audit events through `IAuditTrail`.
- Manager: per-user non-exportable device key in the OS key store (CNG, Secure
  Enclave or Keychain, PKCS#11 token) and one owner-only, device-key-signed
  `registration.state` file; no DPAPI.

**Testing**:

- Platform: xUnit v3, Shouldly, `WebApplicationFactory`
  (`Microsoft.AspNetCore.Mvc.Testing` 10.0.12), Testcontainers PostgreSQL,
  `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing` 10.10.0,
  pinned here only if absent and reused by Durable Analysis Workflow) in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry`; fixture
  manifest and index checks in `SocAlytics.Platform.Contracts.Tests`; layer,
  visibility, and anonymous-allow-list rules in
  `SocAlytics.Platform.Architecture.Tests`.
- Manager: xUnit v3 3.2.2 pinned `[3.2.2,4.0)` (4.x breaks
  `Avalonia.Headless.XUnit`, Avalonia #22072), `xunit.runner.visualstudio`
  3.1.5, `Microsoft.NET.Test.Sdk` 18.0.1, Shouldly 4.3.0,
  `Avalonia.Headless.XUnit` 12.1.3, `FakeTimeProvider`, `FakeLogger`
  (`Microsoft.Extensions.Diagnostics.Testing` 10.10.0), `JsonSchema.Net` 8.0.5
  (last MIT release) in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests`.
  Linux runner: SoftHSM2 PKCS#11, headless UI, socket, XDG autostart; Windows
  and macOS specifics `SkipUnless` their OS and verified manually.

**Target Platform**: Platform API on any .NET 10 host (Linux OCI image in
deployment; Aspire AppHost locally). Manager desktop app for the signed-in user
on Windows 10 1803+ (x64, arm64), macOS 13+ (arm64, x64; Secure Enclave on
Apple silicon and T2), and Linux desktops with glibc ≥ 2.34 (x64; tray requires
a StatusNotifierItem host, otherwise the status window); CI on the Linux runner.

**Project Type**: Web service (platform REST API) plus cross-platform desktop
tray application with an in-process worker and an optional scripting CLI.

**Performance Goals**: SC-001 (Active in under 5 minutes of hands-on time);
revocation observed within one status-check interval (non-production default
60 s, SC-003); restore diagnostics visible within 30 s (SC-005); timed pause
resumes within 5 s of its end (SC-012, 1 s tick); per Manager request at most
one indexed token/registration lookup and one replay insert.

**Constraints**: HTTPS only for Manager traffic (FR-015); platform clock decides
every platform expiry; no secret in logs, audit, status, windows, diagnostics,
or state files (FR-034); no OS service and no administrator rights (FR-037);
never `CKA_EXTRACTABLE=true` on PKCS#11; no Analyst containers, models, NATS,
object storage, installers, code signing, or production runtime integration;
every configurable window and limit has an explicit non-production default and
no production approval.

**Scale/Scope**: One stamp with tens of Managers; 13 REST paths (14
operations), 3 tables, 1 migration; Manager solution with 2 production
projects, 1 test project, and 1 Swift package; one shared fixture set.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-design evaluation

| Gate | Result | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | Follows [Analyst Manager](../../docs/architecture/analyst-manager.md#registration-and-stamp-binding) (Generic Host plus Avalonia tray, two authorized actions, browser pairing, challenge activation, `private_key_jwt` + DPoP, revocation precedence, lifecycle and operating states, restore on autostart), [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status), [Platform Implementation](../../docs/architecture/platform-implementation.md#analyst-technology), [Security and Data Governance](../../docs/architecture/security-and-data-governance.md#audit-events), and [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md#contract-authority) (repository-root `contracts/`). Refinements and the replacement of "operating-system-protected local storage" by a signed owner-only file are listed under [Required Architecture Updates](#required-architecture-updates). Unresolved policy values stay explicit non-production defaults. |
| II. Respect source-area ownership | PASS | Registration records, authorization, credentials, and audit are in `src/platform` Registry folders of existing layer projects (no new platform project). The Manager, its Swift bridge, and its tests are under the planned `src/analysts/manager` path (the analysts area allows several languages). Fixtures live in repository-root `contracts/`, the location the architecture defines for canonical contracts, not under `src/`. |
| III. API-first control plane | PASS | Every human action is an authorized REST operation; the Manager is an API consumer with no database access; no video bytes; OpenAPI 3.1 and JSON Schema 2020-12 contracts are authoritative; the local-control schema lives with the Manager that validates it. |
| IV. Evidence over claims | PASS | Focused platform tests plus host, architecture, and contract tests; Manager tests run on Linux with a real PKCS#11 module (SoftHSM2), headless Avalonia, and real sockets; golden fixtures verified by both sides. Windows CNG and macOS parts are verified manually per [quickstart.md](quickstart.md) and recorded on the pull request before merge; the pull request is held for review until that evidence is attached, which is operational (the user sets `SPECKIT_AUTO_MERGE=false` for this feature or reviews it; no task changes automation; risk R-14). Nothing is claimed as deployment support or production readiness. |
| V. Focused, minimal changes | PASS | One grant, one client type, opaque tokens, in-box rate limiting and sockets, no authorization-server framework; the Manager adds only the spec-required UI framework, one PKCS#11 binding, and a minimal Swift bridge for macOS APIs .NET lacks; no runtime adapter, installer, or autostart library. The Core/UI project split is justified by the UI-free worker requirement. Each package is listed with its purpose. |
| Technology: deferred technologies | PASS | PostgreSQL/Dapper/DbUp come from persistence; human authentication from Club and Identity; machine authentication and Avalonia are adopted by this spec (Avalonia is already the architecture's provisional choice). NATS and S3 are not used. |
| Technology: environment features | PASS, dependent | `.github/actions/environment-verify` builds and tests only `src/platform/SocAlytics.Platform.slnx`, and the runner lacks SoftHSM2. This feature depends on the combined environment feature [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md) (scope below), which must be reviewed and merged first; the spec's Assumptions → Dependencies names it. |
| Workflow and quality gates | PASS | The environment feature may change only the two action folders, so this feature owns every related documentation change (see [Documentation Updates](#documentation-updates)). Markdown passes `node .github/scripts/check-markdown.mjs`. No commits, pushes, or product CI workflows. |

### Required environment feature

- **Feature**: [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md);
  it changes only `.github/actions/environment-setup` and
  `.github/actions/environment-verify`.
- **Manager solution (confirmed)**:
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, containing
  `SocAlytics.Analysts.Manager.Core/SocAlytics.Analysts.Manager.Core.csproj`,
  `SocAlytics.Analysts.Manager/SocAlytics.Analysts.Manager.csproj`, and
  `Tests/SocAlytics.Analysts.Manager.Tests/SocAlytics.Analysts.Manager.Tests.csproj`.
  The Swift package under `native/macos/` is not part of the solution.
- **SDK pin (confirmed)**: own `src/analysts/manager/global.json` with content
  identical to `src/platform/global.json`; both pins change together.
- **Setup**: installs SoftHSM2 (`softhsm2`, module
  `/usr/lib/softhsm/libsofthsm2.so`). No X server, fontconfig, or Xvfb is needed
  for Avalonia headless tests (spike M4); the SDK already comes from the
  platform pin.
- **Verify**: restore, build, and test the Manager solution when a changed
  path matches the environment feature's `MANAGER_TRIGGER`
  (`^(src/analysts/manager|contracts/analyst-manager)/`) and in finalize mode,
  skipping the check while the solution does not exist. Only `MANAGER_SCOPE`
  (`^src/analysts/manager/`) joins `COVERED`; all of `contracts/` belongs to
  the platform scope, so a fixture change under `contracts/analyst-manager/`
  runs the platform check first and then the Manager check (analysis findings
  I1 and X09).
- **Test strategy**: the Linux runner runs the PKCS#11 provider against a
  per-user SoftHSM2 token (`SOFTHSM2_CONF`), headless tray and status-window
  tests, socket and single-instance tests with `SO_PEERCRED`, XDG autostart, the
  signed-state store, and the golden fixtures. Windows CNG, Windows autostart
  and socket ACLs, and the macOS bridge are `SkipUnless` their OS and verified
  manually per quickstart, together with the composed end-to-end run, before
  merge; the pull request is held for review until then (operational, R-14).

### Post-design re-evaluation

| Gate | Result | Notes after Phase 1 |
| --- | --- | --- |
| I | PASS | Data model and contracts reproduce the architecture state machines, including platform `Unregistering`; tray, provider, and signed-state refinements are queued as Required Architecture Updates. |
| II | PASS | Structure below keeps platform files in Registry folders, Manager files under `src/analysts/manager`, and fixtures under `contracts/`; no cross-area project references. |
| III | PASS | 14 operations in [openapi.yaml](contracts/openapi.yaml) (validated as OpenAPI 3.1); disjoint human and machine schemes; [local-control.schema.json](contracts/local-control.schema.json) validated as JSON Schema 2020-12; fixtures tie both sides to the same exchanges. |
| IV | PASS | [quickstart.md](quickstart.md) maps every user story, edge case group, and success criterion to an automated suite or a recorded manual step. |
| V | PASS | Platform +1 production and +1 test package; Manager 8 production packages (4 Core, 4 app) and 8 test packages. No Complexity Tracking entry is needed. |
| Environment | PASS, dependent | Implementation starts only after the environment feature is merged. |

## Project Structure

### Documentation (this feature)

```text
specs/20261005-130704-analyst-manager-registration/
├── plan.md                       # This file
├── research.md                   # Phase 0 decisions
├── data-model.md                 # Phase 1 entities and state machines
├── quickstart.md                 # Phase 1 validation guide
├── contracts/
│   ├── openapi.yaml              # Platform REST operations (OpenAPI 3.1)
│   ├── proof-profiles.md         # Activation proof, client assertion, DPoP profiles; fixture vectors
│   ├── local-control.schema.json # Manager local-control messages (JSON Schema 2020-12)
│   └── local-control.md          # Tray menu, status window, single instance, CLI
└── tasks.md                      # Created by /speckit-tasks
```

### Source Code (repository root)

Shared golden fixtures (FR-039):

```text
contracts/
├── README.md                                        # + index row (index created by Recording Lineage and Upload)
└── analyst-manager/registration/v1/
    ├── registration.schema.json                     # $id https://socalytics.invalid/contracts/analyst-manager/registration/v1/registration.schema.json (manifest schema)
    ├── fixtures.json                                # Manifest: fixed clock, stamp, base URI, vectors, expected outcomes
    ├── test-key.jwk.json                            # RFC 7515 Appendix A.3 P-256 example key (public test vector)
    ├── fingerprint.json                             # Test-key thumbprint and device fingerprint
    ├── submission.request.json, submission.wrong-fingerprint.request.json
    ├── stamp-discovery.response.json
    ├── pairing.request.json, pairing.response.json
    ├── pairing-status.{awaiting,pending,approved,rejected}.response.json
    ├── activation.request.json, activation.response.json
    ├── token.request.json, token.response.json, token.inactive.response.json
    ├── self-registration.request.json, self-registration.response.json,
    │   self-registration.inactive.response.json
    └── negative/                                    # Replayed jti, wrong aud, wrong key, stale iat, wrong htu, missing ath
```

Platform part (modified and new files only):

```text
src/platform/
├── Directory.Packages.props                         # + JsonWebTokens, TimeProvider.Testing
├── SocAlytics.Platform.Domain/Registry/
│   ├── AnalystManagerRegistration.cs                # Lifecycle rules and transitions
│   ├── RegistrationState.cs
│   ├── DeviceKeyThumbprint.cs
│   ├── DeviceMetadata.cs                            # Includes ClaimedKeyProtection
│   ├── DeviceFingerprint.cs                         # Derivation from the thumbprint (proof-profiles.md)
│   └── RegistrationWindows.cs
├── SocAlytics.Platform.Application/Registry/
│   ├── RegistryOptions.cs                           # Windows, lifetimes, limits, stamp
│   ├── Abstractions/                                # IRegistrationStore, IManagerAccessTokenStore,
│   │                                                # IProofReplayStore, IManagerProofValidator,
│   │                                                # IRegistrySecretGenerator
│   ├── Pairing/                                     # StartPairing, GetPairingStatus, ActivateRegistration
│   ├── Decisions/                                   # SubmitPairingCode, ApproveRegistration,
│   │                                                # RejectRegistration, RevokeRegistration,
│   │                                                # ListRegistrations, GetRegistration
│   ├── MachineAccess/                               # IssueManagerToken, AuthenticateManagerRequest,
│   │                                                # GetOwnRegistration, BeginUnregistration,
│   │                                                # CompleteUnregistration
│   └── Expiry/ExpireDueRegistrations.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Registry/                                    # Dapper stores, JoseManagerProofValidator,
│   │                                                # RegistrySecretGenerator
│   └── Persistence/
│       ├── Migrations/NNNN_registry_analyst_manager_registration.sql
│       └── PostgresAuditTrail.cs                    # + Registry event types, details keys, actor kind
├── SocAlytics.Platform.Api/
│   ├── Program.cs                                   # Calls AddRegistryApi and MapRegistryEndpoints once
│   ├── Security/AuthorizationPolicyNames.cs         # + AnalystManager = "AnalystManager"
│   ├── appsettings.Development.json                 # Stamp:Id local-dev-stamp, Stamp:PublicBaseUri
│   └── Registry/
│       ├── RegistryApiRegistration.cs               # AddRegistryApi (options, AnalystManagerDPoP scheme,
│       │                                            # AnalystManager policy, rate limiter, worker) and
│       │                                            # MapRegistryEndpoints
│       ├── AnalystManagerClaimTypes.cs              # client_id, socalytics:registration_id, scope
│       ├── AnalystManagerDPoPDefaults.cs            # AuthenticationScheme, ScopeValue analyst-manager
│       ├── AnalystManagerPairingEndpoints.cs        # Stamp discovery, pairings, status, activation
│       ├── AnalystManagerTokenEndpoint.cs
│       ├── AnalystManagerSelfEndpoints.cs
│       ├── AnalystManagerRegistrationEndpoints.cs   # Human submission and decisions
│       ├── AnalystManagerDPoPAuthenticationHandler.cs
│       ├── RequireHttpsEndpointFilter.cs
│       ├── RegistryRateLimiting.cs
│       └── RegistrationExpiryWorker.cs
├── SocAlytics.Platform.AppHost/Program.cs           # Stamp__Id, Stamp__PublicBaseUri (HTTPS endpoint, or HTTP when SocAlytics:ApiHttpsEndpoint=false), Registry__AllowSoftwareKeys=true (development)
└── Tests/
    ├── SocAlytics.Platform.Architecture.Tests/      # Registry visibility; anonymous allow list entries;
    │                                                # AnalystManager endpoints exempt from anti-forgery
    ├── SocAlytics.Platform.Contracts.Tests/AnalystManagerRegistrationFixtureTests.cs  # Manifest against registration.schema.json via ContractCatalog
    ├── SocAlytics.Platform.Host.Tests/              # + the 14 operationIds in /openapi/v1.json
    └── SocAlytics.Platform.Integration.Tests/
        ├── Structure/PersistedTableClassifications.cs  # + 2 unversioned tables (registration is versioned)
        └── Registry/
            ├── Support/                             # TestAnalystManager (in-memory ECDSA proofs, DPoP,
            │                                        # assertions), RegistryTestHost
            ├── Unit/                                # RegistrationStateMachineTests, DeviceIdentityTests,
            │                                        # RegistrySecretGeneratorTests
            ├── RegistrySchemaTests.cs
            ├── RegistrationStoreTests.cs
            ├── RegistryAuditEventTests.cs
            ├── ProofValidationTests.cs
            ├── PairingTests.cs
            ├── SubmissionTests.cs
            ├── DecisionTests.cs
            ├── ActivationTests.cs
            ├── ExpiryTests.cs
            ├── RateLimitingTests.cs
            ├── TokenTests.cs
            ├── ManagerAuthenticationTests.cs
            ├── RevocationTests.cs
            ├── UnregistrationTests.cs
            ├── GoldenFixtureTests.cs                # Runs every fixture vector through the API
            └── AuditAndSecretTests.cs
```

Analyst Manager part (all new except `src/analysts/README.md`):

```text
src/analysts/
├── README.md                                        # Updated: manager is executable
└── manager/
    ├── README.md                                    # Run, controls, key stores, autostart, status
    ├── global.json                                  # Same SDK as src/platform/global.json
    ├── Directory.Build.props                        # net10.0, nullable, warnings as errors
    ├── Directory.Packages.props                     # Central versions; xunit.v3 [3.2.2,4.0)
    ├── SocAlytics.Analysts.Manager.slnx
    ├── contracts/local-control.schema.json          # Component-local executable contract
    ├── native/macos/SocAlyticsMacBridge/            # Swift package (macOS build only): Secure Enclave and
    │                                                # Keychain keys, SMAppService autostart, @_cdecl exports
    ├── SocAlytics.Analysts.Manager.Core/            # Class library, no Avalonia reference
    │   ├── Hosting/                                 # ManagerHost, options, DI
    │   ├── DeviceKeys/                              # IDeviceKeyProvider, IDeviceKey, KeyProtection,
    │   │                                            # WindowsCngKeyProvider, MacBridgeKeyProvider,
    │   │                                            # Pkcs11KeyProvider, Pkcs11LibdlResolver,
    │   │                                            # Pkcs11ModuleAllowList, DeviceKeyProviderSelector
    │   ├── LocalState/                              # SignedStateStore, ProtectedRegistrationState,
    │   │                                            # CanonicalJson, OwnerOnlyFiles
    │   ├── Platform/                                # PlatformClient, ProofFactory, token cache, signals
    │   ├── Registration/                            # RegistrationCoordinator, PairingSession,
    │   │                                            # RegistrationRestorer, RegistrationStatusMonitor,
    │   │                                            # ProtectedStoreFailureHandler
    │   ├── Operating/                               # OperatingStateMachine, OperatingIntent,
    │   │                                            # TimedPauseScheduler, AdmissionGate,
    │   │                                            # DrainCoordinator, IManagerWorkTracker
    │   ├── Runtime/                                 # IContainerRuntimeAdapter, RuntimeAdapterCatalog,
    │   │                                            # PreflightRunner, RuntimeCompatibilityList,
    │   │                                            # runtime-compatibility.json (embedded, empty)
    │   ├── Autostart/                               # IAutostartRegistrar, WindowsRunKeyRegistrar,
    │   │                                            # MacAutostartRegistrar, XdgAutostartRegistrar
    │   ├── LocalControl/                            # LocalControlServer, LocalControlClient,
    │   │                                            # PeerCredentialVerifier, SingleInstanceGuard
    │   └── Diagnostics/                             # Status snapshot, bounded diagnostic codes
    ├── SocAlytics.Analysts.Manager/                 # Avalonia 12.1.3 WinExe, assembly socalytics-manager
    │   ├── Program.cs                               # CLI dispatch (System.CommandLine) or tray start; --autostart
    │   ├── App.axaml, App.axaml.cs                  # OnExplicitShutdown, TrayIcon, session-end handling
    │   ├── Tray/                                    # TrayMenuBuilder, TrayHostDetector (StatusNotifierWatcher)
    │   ├── Views/                                   # StatusWindow, RegisterWindow, CustomPauseDialog
    │   ├── ViewModels/                              # TrayMenu, StatusWindow, Register view models
    │   └── Assets/                                  # Tray icons
    └── Tests/SocAlytics.Analysts.Manager.Tests/
        ├── TestAppBuilder.cs                        # [assembly: AvaloniaTestApplication], UseHeadless
        ├── Support/                                 # SoftHsmToken, FakePlatform, InMemoryDeviceKeyProvider,
        │                                            # SimulatedRuntimeAdapter, SimulatedWork, ManagerTestHost
        ├── DeviceKeys/                              # PKCS#11 (SoftHSM2); CNG and macOS (SkipUnless)
        ├── LocalState/
        ├── Registration/
        ├── Restore/
        ├── Revocation/
        ├── Preflight/
        ├── Operating/                               # Includes timed pause and session end
        ├── Autostart/
        ├── LocalControl/
        ├── Ui/                                      # Headless tray menu and status window, view models
        ├── GoldenFixtures/
        └── Secrets/
```

Repository documentation touched by implementation is listed in
[Documentation Updates](#documentation-updates).

**Structure Decision**: The platform part follows the layered monolith: one
`Registry` folder per layer in the existing projects, tests in the shared
Integration, Contracts, and Architecture test projects, and no new platform
project. The Analyst Manager is a separate .NET solution under
`src/analysts/manager` with a UI-free Core library (worker and all behavior), an
Avalonia tray executable, one test project, and a macOS-only Swift package. The
two solutions share no project references; they are tied together by
[contracts/openapi.yaml](contracts/openapi.yaml) and the golden fixtures in
repository-root `contracts/`, which both test suites read.

## Implementation Notes

- **Authorization**: Registry commands call `IAccessAuthorizer.AuthorizeClubAsync`
  at execution time (`ClubPermission.Register` for submission;
  `ClubPermission.Administer` for approve, reject, revoke, list, and read); the
  authorizer records each denial once as `authorization.denied`. Human endpoints
  use `MapMemberApi` (`SocAlyticsSession`, `ActiveMember`) and
  `SessionAntiforgeryFilter` (`X-CSRF-Token`). Anonymous Manager endpoints are
  added to the Club and Identity anonymous allow list; `self` endpoints require
  the policy `AuthorizationPolicyNames.AnalystManager` = `"AnalystManager"`
  (scheme `AnalystManagerDPoPDefaults.AuthenticationScheme` =
  `"AnalystManagerDPoP"`, scope `analyst-manager`), which Durable Analysis
  Workflow reuses. The authenticated principal carries exactly the claims of
  `AnalystManagerClaimTypes` (`public static` in `SocAlytics.Platform.Api`,
  folder `Registry/`): `ManagerId = "client_id"` (the Manager id),
  `RegistrationId = "socalytics:registration_id"`, and `Scope = "scope"` with
  value `analyst-manager`; consumers use these constants, never literals.
  Neither family
  accepts the other's scheme (FR-013). Handlers return `OperationResult<T>`;
  `ProblemResults` maps failures to `urn:socalytics:problem:<code>`.
- **Audit**: `IAuditTrail.RecordAsync` inside the transition's
  `IUnitOfWorkScope`, or `RecordIndependentAsync` for refusals that change
  nothing; event types, actor kinds, and the allow-listed details are in
  [data-model.md](data-model.md#audit-events-security_audit_event).
- **Concurrency**: lifecycle transitions use state guards
  (`WHERE id = @Id AND state = @ExpectedState`), never `If-Match`; reads return
  a strong ETag of `version`. A lost race on code submission returns the
  indistinguishable refusal; other lost races return `409`.
- **Expiry**: inline checks in every command plus `RegistrationExpiryWorker`,
  which also purges expired token and replay rows.
- **Secrets**: generated with `RandomNumberGenerator`, returned once, stored as
  SHA-256 hashes, never placed in log templates, exception messages, or problem
  details; tests assert their absence on both sides.
- **Manager startup order**: single-instance check on the socket (hand off with
  `show-status` if another instance answers), start the worker and the tray
  (status window if no tray host), read and verify the signed state (fail closed
  on any defect), obtain a token (assertion plus DPoP), confirm
  `self/registration`, run preflight, then enter the restored intent including
  an unexpired timed pause. Admission is possible only in `Running` after all
  steps succeeded in the current process.
- **Key providers**: TPM-first on Windows; Secure Enclave first on macOS;
  allow-listed PKCS#11 module on Linux; software-backed fallbacks only when
  stamp discovery reports `softwareKeysAllowed`. The provider's
  `keyProtection` is sent as `claimedKeyProtection` and stored in the signed
  state.
- **Phishing resistance (FR-040)**: the register dialog shows the pairing code
  and the device fingerprint together; submission requires both; the
  platform compares the fingerprint in constant time, answers a mismatch with
  the generic refusal, audits it, keeps the code, and expires the pairing after
  `Registry:Pairing:MaxFingerprintMismatches`; registration reads return the
  fingerprint, `pairingOrigin`, and `approvalNotice` (research R25).
- **Claimed key protection (FR-041)**: labeled "claimed" in API, audit, and
  views; no authorization or approval logic reads it; its only effect is the
  `software` refusal unless `Registry:AllowSoftwareKeys` (default `false`) is
  true. Hardware attestation is deferred (research R26, R-13).
- **Inactive signal**: the Manager clears stamp state and deletes the device key
  only after a proof-authenticated inactive response (research R17).
- **Local unregister**: drain first; only a successful drain leads to
  `self/unregistration` and `self/unregistration/completion`. A drain cancelled
  by the `CancelRequest` timeout policy makes no platform call, keeps the
  registration Active, clears `pendingUnregister`, and restores the previous
  intent (spec clarification 2026-10-07, research R18).
- **Protected store lost at runtime**: a key or signed-state failure while
  Active closes admission and moves the Manager to `restore-failed` with the
  diagnostic code; nothing falls back to unprotected storage.

## Documentation Updates

Implementation tasks of this feature make these changes (constitution:
supported commands are documented in `README.md`, and `AGENTS.md` stays
current). The environment feature changes only the two action folders, so it
cannot make them. Each change describes behavior that exists once both features
are merged; where another feature edits the same item, whichever merges second
combines the sentences.

1. **`README.md`, section "Requesting Implementation on GitHub", list "In this
   repository:" under the composite actions** (around lines 222–228): replace
   the two items with:

   > - `environment-setup` installs .NET from `src/platform/global.json`, the
   >   Markdown linters, and SoftHSM2 for the Analyst Manager PKCS#11 tests;
   >   Docker for Testcontainers is already on the runner;
   > - `environment-verify` runs the platform restore, build, and tests when
   >   `src/platform/**` or `contracts/**` changed, the Analyst Manager
   >   restore, build, and tests
   >   (`src/analysts/manager/SocAlytics.Analysts.Manager.slnx`) when
   >   `src/analysts/manager/**` or the shared fixtures under
   >   `contracts/analyst-manager/**` changed, every check in finalize mode,
   >   and the Markdown check when Markdown changed. It lists the paths it
   >   covers in `COVERED` (`contracts/` counts as platform-covered);
   >   changed files outside them are reported as not covered, including
   >   platform or Manager files when their solution is missing.

   The implementing task states the trigger paths exactly as
   `.github/actions/environment-verify/action.yml` defines them when it runs
   (its platform scope, `MANAGER_SCOPE`, and `MANAGER_TRIGGER`) and corrects this
   text if they differ.

2. **`README.md`, section "Development"**: add a subsection after "Platform
   Host":

   > ### Analyst Manager
   >
   > The Analyst Manager is a per-user desktop tray application for Windows,
   > macOS, and Linux in a separate .NET 10 solution that pins the same SDK as
   > the platform in its own `global.json`. Run these commands from the
   > repository root:
   >
   > ```powershell
   > dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
   > dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
   > dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
   > dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager
   > ```
   >
   > The tray menu (or the status window where no tray area exists) offers
   > status, pause, timed pause, resume, safe exit, register, unregister, and
   > start at sign-in. `socalytics-manager status`, `pause`, `resume`, `exit`,
   > `register`, `unregister`, and `autostart` script a running Manager.
   > Device keys are non-exportable: Windows CNG (TPM), macOS Secure Enclave
   > (requires the Swift bridge built on macOS), and Linux PKCS#11
   > (tpm2-pkcs11); software-backed keys are used only where the stamp allows
   > them (development and test). Registration shows a device fingerprint that
   > the Registrar enters together with the pairing code. The Linux tests need SoftHSM2 (`softhsm2`); Windows
   > and macOS key-store tests are skipped elsewhere and verified manually. No
   > production container-runtime integration ships, so a Manager stays Runtime
   > unavailable. This is development evidence, not production readiness.

3. **`README.md`, section "Repository Structure"**: the `src/analysts/` item
   says it contains the executable Analyst Manager desktop app under
   `src/analysts/manager/` and the planned Analyst SDK and capability boundary;
   the paragraph after the list says the clients and agents areas remain
   non-executable while the platform and the Analyst Manager contain
   application projects and tests; add the golden-fixture set to the
   `contracts/` item (adding the item only if Recording did not list it); in the "Platform Host" evidence paragraphs, add
   Analyst Manager registration (pairing, approval, activation, DPoP machine
   tokens, revocation).

4. **`AGENTS.md`**:
   - "Current State": the analysts area is executable for
     `src/analysts/manager` (tray app, Core worker, tests, Swift bridge); add
     Analyst Manager registration, machine authentication, and the shared
     fixtures to the executable evidence, keeping the statement that no
     production-readiness evidence exists.
   - "Repository Setup": add the four Manager commands from item 2 with notes
     that the Manager has its own `global.json` pinned to the platform SDK, that
     xUnit v3 stays below 4.0 while `Avalonia.Headless.XUnit` requires it, that
     Linux tests need SoftHSM2, and that Windows and macOS key-store tests are
     skipped elsewhere and verified manually.

5. **`src/analysts/README.md`**: "Current Status" and the scaffold sentence in
   "Exclusions" state that `manager/` contains the executable Analyst Manager
   (no SDK or capabilities yet) and link `manager/README.md`.

6. **`src/analysts/manager/README.md`** (new): purpose, run and test commands,
   tray and CLI controls with exit codes from
   [contracts/local-control.md](contracts/local-control.md), key providers per
   OS and how to provision a tpm2-pkcs11 token (including the random user PIN
   passed to `tpm2_ptool addtoken --userpin` and stored by the operator in the
   owner-only `pkcs11.pin`), autostart per OS, building the
   macOS bridge, non-production configuration defaults, and the readiness
   disclaimer.

7. **`src/platform/README.md`**: Registry endpoints, `Stamp:Id` and
   `Stamp:PublicBaseUri`, the Registry option defaults including
   `Registry:AllowSoftwareKeys` (false; true only for development and test) and
   `Registry:Pairing:MaxFingerprintMismatches`, the `AnalystManagerDPoP`
   scheme, the `AnalystManager` policy, the `AnalystManagerClaimTypes`
   constants, and the fixture tests.

8. **`contracts/README.md`** (index created by Recording Lineage and Upload,
   which merges first; this feature only adds a row): index row
   `analyst-manager/registration/v1/registration.schema.json`, owner "Registry
   (control plane) and Analyst Manager", version `1.0.0`, example "the manifest
   `fixtures.json` and the fixture files it lists, plus the schema's embedded
   `examples`", validation commands
   `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` and the
   Manager test command. The existing `ContractIndexTests` (one row per schema,
   `$id` equals path, path rule) and `SchemaMetaValidationTests` cover the new
   schema unchanged.

## Required Architecture Updates

The coordinator applies these; this plan does not edit `docs/architecture/`.
They replace the earlier headless and CNG/DPAPI proposals.

1. **`docs/architecture/analyst-manager.md`, section "Desktop Application
   Profile"**, replace the paragraph that begins "The provisional desktop
   implementation uses a .NET 10 Generic Host" and the following "Promotion
   requires an Avalonia spike" paragraph with:

   > The Analyst Manager is a per-user desktop application: it runs in the
   > context of the signed-in user on Windows, macOS, and Linux, without an
   > operating-system service and without administrator rights. A .NET 10
   > Generic Host worker without UI dependencies runs in-process with an
   > Avalonia tray application. The tray menu offers status, pause, pause for a
   > chosen time with automatic resume, resume, safe exit, register,
   > unregister, and start at sign-in; where the desktop has no tray host (for
   > example Linux without StatusNotifierItem support), a status window offers
   > the same controls. One owner-only local socket per user provides
   > single-instance hand-off and an optional scripting client; the Manager
   > verifies that every peer belongs to the same user and never listens on the
   > network. Autostart is opt-in and per user: the Windows `Run` key of the
   > current user, `SMAppService` (or a per-user launch agent) on macOS, and an
   > XDG autostart entry on Linux. The 2026-10 spike confirmed the Avalonia
   > tray, headless UI tests, and the local socket; installers, signing,
   > updates, and accessibility remain open promotion items, with Electron or
   > Tauri as the evaluated alternatives if a required platform capability
   > fails.

2. **`docs/architecture/analyst-manager.md`, section "Browser Pairing And
   Approval"**, append after the sentence "Hardware-backed storage may be used
   but is not required.":

   > Supported keystores are per-user and non-exportable: on Windows a CNG key
   > in the Microsoft Platform Crypto Provider (TPM) when available, otherwise
   > in the software key storage provider; on macOS a Secure Enclave key when
   > available, otherwise a Keychain key, reached through a small native bridge
   > because .NET has no Secure Enclave API; on Linux a key in a PKCS#11 token,
   > with tpm2-pkcs11 as the production module, generated without the
   > extractable attribute because non-exportability comes from the TPM. The
   > AM reports the kind of protection at registration; without attestation the
   > platform records and shows it only as claimed and never uses it for
   > authorization, but refuses software-backed keys unless the stamp
   > configuration allows them, which is intended for development and test
   > only. Hardware key attestation and production approval of any store
   > remain governed by `GOV-CRED-002` in Security and Data Governance (link
   > target `security-and-data-governance.md`).
   >
   > To resist remote phishing of pairing codes, the AM shows a short device
   > fingerprint derived from its public key next to the pairing code. The
   > Registrar must enter both; a fingerprint that does not match the paired
   > key is refused like an invalid code. The approval view shows the
   > fingerprint, the time and network origin of the pairing request, and a
   > warning to approve only Managers the approver can physically identify.

3. **`docs/architecture/analyst-manager.md`, section "Machine
   Authentication"**:
   - Append after the sentence "AM tokens cannot authorize human or
     administrative APIs.":

     > The DPoP proof key is the registered device key, so every token request
     > and API call proves possession of the non-exportable key. Access tokens
     > are opaque references stored only as hashes and validated against the
     > authoritative registration on every call, so revocation takes effect
     > without a platform token-signing key. The AM treats its registration as
     > inactive only after a refusal that follows its own valid key proof;
     > transport failures never clear its protected registration.

   - Replace the sentence that begins "The AM stores its Manager identity,
     endpoint, stamp binding, and key reference in operating-system-protected
     local storage" (keep the rest of that paragraph) with:

     > The AM stores its Manager identity, endpoint, stamp binding, key
     > reference, and last operating intent in a local file readable only by
     > the user's account and signed with the device key, and restores them
     > across restart and autostart; any change, or a copy to another account
     > or machine, fails verification and the AM fails closed.

4. **`docs/architecture/platform-implementation.md`, section "API And
   Identity"**, append after the paragraph that begins "An active AM uses OAuth
   2.0 client credentials":

   > The Analyst Manager token endpoint is a minimal endpoint in the API's
   > Registry functional area that supports only this grant; the platform does
   > not run a general-purpose authorization server for machine clients.

5. **`docs/architecture/platform-implementation.md`, section "Analyst
   Technology"**, replace the first paragraph with:

   > The Analyst Manager is a per-user desktop application in a separate .NET
   > solution under `src/analysts/manager`. A .NET 10 Generic Host worker in a
   > library without UI dependencies owns lifecycle, registration, key
   > providers, signed local state, queue, hardware, autostart, and
   > OCI-runtime responsibilities; Avalonia supplies the cross-platform tray
   > and status UI in the same process. Device keys use Windows CNG, a small
   > Swift bridge over CryptoKit and the Keychain on macOS, and PKCS#11 through
   > Pkcs11Interop on Linux. The worker and UI are tested without a display
   > using Avalonia headless tests; Linux CI uses a SoftHSM2 token, while the
   > Windows and macOS key stores are verified on those systems.

6. **`docs/architecture/platform-implementation.md`, section "Source And
   Runtime Baseline"**, replace the `src/analysts` bullet with:

   > `src/analysts` owns the Analyst Manager, whose first desktop slice is
   > executable under `src/analysts/manager`, and the future Analyst SDK and
   > Analyst capability implementations across their required runtimes

## Risk Register

| ID | Risk | Disposition | Evidence / Owner | Revisit trigger |
| --- | --- | --- | --- | --- |
| R-01 | Windows code (CNG providers, Run-key autostart, socket ACL and SID peer check) is untested in CI | Accepted | The composite actions run on the Linux runner only; the code sits behind `IDeviceKeyProvider`, `IAutostartRegistrar`, and the peer verifier with `SkipUnless` tests, and is verified manually per [quickstart.md](quickstart.md) section 3 before merge (spike M2 and M6 evidence on Windows) | A Windows CI job is added |
| R-02 | macOS Secure Enclave bridge needs a Mac spike and possibly entitlements (`keychain-access-groups`, signed app) | Deferred | Owner: Analyst Manager implementation, before any macOS release; until then macOS fails closed when the bridge is absent (spike M3 research only) | Mac spike result, or a macOS release is planned |
| R-03 | Linux desktops without a StatusNotifierItem host show no tray icon | Mitigated | Status window fallback with the same controls plus the CLI (R11, spike M4); headless tests cover the fallback | Avalonia adds another Linux tray protocol, or operators report missing controls |
| R-04 | tpm2-pkcs11 lets callers flip `CKA_EXTRACTABLE`/`CKA_SENSITIVE` metadata | Mitigated | The provider never sets `CKA_EXTRACTABLE=true` or `CKA_SENSITIVE=false`; non-exportability comes from the TPM (fixed-TPM and fixed-parent object attributes, spike M1); the store directory is owner-only | tpm2-pkcs11 changes attribute handling, or a TPM-blob check at load time is required |
| R-05 | No production container-runtime adapter, so every real Manager stays Runtime unavailable | Accepted | Spec scope: preflight is validated with a simulated runtime; FR-032 requires Runtime unavailable without an integration | The Docker Desktop adapter feature is planned |
| R-06 | Rate limits are per API instance and forwarded headers are not configured (client address and HTTPS detection behind a proxy) | Deferred | Owner: production operations profile (`docs/architecture/production-operations.md`) and deployment work | A production deployment profile is drafted |
| R-07 | Drift between the platform and Manager implementations of the registration exchanges | Mitigated | Golden fixtures under `contracts/analyst-manager/registration/v1/` verified by both test suites (FR-039, R23) plus the composed manual end-to-end run before merge | Any change to [openapi.yaml](contracts/openapi.yaml) or [proof-profiles.md](contracts/proof-profiles.md) |
| R-08 | Club and Identity name drift (cookie, header, problem types, shared types, tables) | Mitigated | All names reconciled with the finished Club and Identity plan and tasks (`PlatformApiFactory`, the `PostgresAuditTrail` allow-list, `ck_security_audit_event_actor_kind`, `SocAlytics:ApiHttpsEndpoint`, `OperationFailure.Validation`/`Conflict`, the `AuthorizeClubAsync(ClubPermission, AuditResource, CancellationToken)` overload); this feature adds no Club and Identity types (see [Dependencies and Coordination](#dependencies-and-coordination)) | Club and Identity plan or contract changes |
| R-09 | xUnit v3 4.x breaks `Avalonia.Headless.XUnit` | Mitigated | Central pin `[3.2.2,4.0)` (spike M4 reproduced the failure; Avalonia #22072) | Avalonia #22072 is resolved |
| R-10 | Software-backed keys (Windows software KSP, macOS Keychain without Secure Enclave) roam with roaming profiles or can be copied with the user profile | Mitigated | Refused by default: `Registry:AllowSoftwareKeys` is `false` outside development and test, discovery tells the Manager, and the Manager does not fall back to a software key (FR-041, research R26; spike M2 roaming evidence) | A production profile asks to allow software keys, or attestation lands (R-13) |
| R-11 | The `contracts/` index or `SocAlytics.Platform.Contracts.Tests` might not exist when this feature is implemented | Resolved | Recording Lineage and Upload is now a declared predecessor (spec clarification 2026-10-07; merge order environment and persistence → Club and Identity → Recording → this feature → Durable Analysis); it owns the index, the project, `ContractCatalog`, and the generic schema checks, and this feature only adds a schema, an index row, and fixture tests (T002) | The merge order changes |
| R-12 | Device-code phishing (RFC 8628 §5.4): a Registrar is tricked into submitting a pairing code for an attacker's Manager | Mitigated | Device fingerprint entered with the code and verified server-side with an indistinguishable refusal and a mismatch limit; pairing origin, time, and approval notice in decision views; separate Club Admin approval (FR-040, research R25). Residual: a Registrar who types both values from an attacker's message; owner of residual-risk acceptance: Security and Data Governance (THR-003) | Phishing reports, or QR pairing is introduced |
| R-13 | `claimedKeyProtection` is self-reported; a modified Manager can claim hardware protection for a software key | Deferred | Never used for authorization and labeled claimed (FR-041). Hardware attestation (Windows Platform Crypto Provider key attestation, `TPM2_Certify` for tpm2-pkcs11; none generally available for macOS Secure Enclave keys) is deferred; owner: credential and key authority under `GOV-CRED-002` | `GOV-CRED-002` requires hardware-bound Manager keys for production |
| R-14 | Automation could merge the pull request before the manual Windows, macOS, and composed end-to-end evidence (quickstart sections 3 and 4) is recorded, and the Swift bridge is never compiled on the Linux runner | Mitigated (operational) | Spec clarification 2026-10-07: the pull request is always held for review and merges only after that evidence is recorded on it. The hold is operational: the user sets `SPECKIT_AUTO_MERGE=false` for this feature or reviews the pull request before merge; no task changes automation | A Windows or macOS CI job is added, or the merge policy changes |

## Dependencies and Coordination

- **Environment**: [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md)
  must be merged first (SoftHSM2 in setup, Manager and `contracts/analyst-manager`
  coverage in verify).
- **Platform Persistence Foundation**: `IUnitOfWork.BeginAsync` →
  `IUnitOfWorkScope`, migrations and Migrator, `socalytics.advance_version()` via
  `socalytics.attach_version_trigger(regclass)`,
  `Structure/PersistedTableClassifications.cs`, the structural test, and the
  `Integration.Tests` project.
- **Club and Identity Foundation**: the shared names listed under Primary
  Dependencies; `member_account(id)` as the foreign-key target of
  `submitted_by`, `decided_by`, and `revoked_by`; the `club` table for the club
  name in Manager status; the AppHost HTTPS endpoint; and the anonymous allow
  list in the architecture tests.
- **Club and Identity types used as provided** (this feature adds or changes
  none of them):
  - `IAccessAuthorizer.AuthorizeClubAsync(ClubPermission, AuditResource, CancellationToken)`
    (Club and Identity T008) names the registration in `authorization.denied`
    (FR-033).
  - `OperationFailure.Validation(code, …)` and `OperationFailure.Conflict(code)`
    (Club and Identity T002) carry the coded `400` and `409` failures
    (`analyst-manager-pairing-code-invalid`,
    `analyst-manager-polling-handle-invalid`,
    `analyst-manager-activation-proof-invalid`, and the `409` codes); the
    `PostgresAuditTrail` details allow-list is extended for the Registry keys.
  - The `security_audit_event` constraint `ck_security_audit_event_actor_kind`
    is dropped and re-added under the same name with `analyst-manager` added;
    Club and Identity has no `resource_type` check, so none is altered; the
    Registry migration applies this.
- **Recording Lineage and Upload** (declared predecessor; merges before this
  feature): provides the `contracts/` index, `SocAlytics.Platform.Contracts.Tests`
  with `ContractCatalog` and the generic contract checks, and `JsonSchema.Net`
  8.0.5 in the platform package versions. This feature takes the next free
  migration number on an up-to-date `main` after Recording's migration, and
  its additive edits to shared files (`Directory.Packages.props`,
  `Api/Program.cs`, `AddInfrastructure()`, `PersistedTableClassifications.cs`,
  `appsettings.Development.json`, `README.md`, `AGENTS.md`) extend Recording's
  content without replacing it.
- **Durable Analysis Workflow** (merges after this feature): consumes the
  `AnalystManagerDPoP` scheme, the `AnalystManager` policy, the
  `AnalystManagerClaimTypes` constants, and the
  `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 pin defined here.
- **AppHost**: this feature adds only the stamp configuration (`Stamp__Id`,
  `Registry__AllowSoftwareKeys=true`, and `Stamp__PublicBaseUri` from the API
  HTTPS endpoint when the Club and Identity flag `SocAlytics:ApiHttpsEndpoint`
  is true (default), otherwise from the API HTTP endpoint; the host smoke test
  runs with the flag false and asserts that the API still receives a
  `Stamp__PublicBaseUri`). `StampOptions` accepts an `http` origin only in the
  `Development` environment; Manager-facing endpoints still require HTTPS.

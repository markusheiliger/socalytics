# Implementation Plan: Analyst Manager Registration

**Branch**: `20261005-130704-analyst-manager-registration` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261005-130704-analyst-manager-registration/spec.md`

## Summary

Deliver the first testable Analyst Manager slice in two parts that meet only at
the versioned REST contract.

- **Platform (`src/platform`, Registry functional area)**: device-code-style
  pairing (high-entropy single-use pairing code and polling handle, hash-only
  storage, indistinguishable refusals, rate limiting), Registrar submission and
  Club Admin approve/reject/revoke as separately authorized, state-guarded,
  audited lifecycle commands, activation by a signed single-use platform
  challenge, durable registration persistence with the full lifecycle state
  machine, and a minimal in-API OAuth 2.0 token endpoint (`client_credentials` +
  `private_key_jwt` + mandatory DPoP bound to the device key) that issues
  short-lived opaque access tokens validated against the authoritative
  registration and stamp binding on every Manager request.
- **Analyst Manager (`src/analysts/manager`)**: a headless .NET 10 Generic Host
  worker and `socalytics-manager` command line that talks to the worker over an
  owner-only Unix domain socket; non-exportable ECDSA P-256 device keys in the
  Windows CNG key storage provider (fail-closed refusal on operating systems
  without a supported store); DPAPI-protected local registration state with
  restart restore, re-proof, and platform confirmation; revocation detection;
  local pause/resume/safe exit/unregister with bounded drain; and a container
  runtime preflight against a built-in compatibility list, validated with a
  simulated runtime because no production runtime integration ships in this
  slice.

Design rationale: [research.md](research.md). Entities and state machines:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 for both parts; SDK 10.0.400
(`rollForward: latestPatch`) from `src/platform/global.json`, pinned identically
in `src/analysts/manager/global.json`; nullable enabled, warnings as errors.

**Primary Dependencies**:

- Platform: ASP.NET Core minimal APIs, built-in OpenAPI, in-box
  `Microsoft.AspNetCore.RateLimiting`; Npgsql, Dapper, DbUp, `IUnitOfWork`, and
  the version triggers from Platform Persistence Foundation; from Club and
  Identity Foundation the `SocAlyticsSession` scheme (cookie
  `__Host-socalytics-session`), `ActiveMember` policy, `MapMemberApi`,
  `SessionAntiforgeryFilter` (`X-CSRF-Token`), `ProblemResults`,
  `OperationResult<T>`/`OperationFailure`, `IRequestContext`,
  `IAccessAuthorizer`/`ClubPermission`, `IAuditTrail`/`AuditEvent`, and the
  `member_account` and `club` tables;
  new package `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 (Infrastructure)
  for JWS validation and RFC 7638 thumbprints. No OpenIddict or Duende
  (research R1).
- Manager: `Microsoft.Extensions.Hosting` 10.0.12, `Microsoft.Extensions.Http`
  10.0.12, `System.CommandLine` 2.0.12, `Microsoft.IdentityModel.JsonWebTokens`
  8.23.0, `System.Security.Cryptography.ProtectedData` 10.0.12; Windows CNG and
  `System.Net.Sockets` Unix domain sockets are in-box.

**Storage**:

- Platform: stamp PostgreSQL schema `socalytics`, three new tables
  (`analyst_manager_registration` versioned root, `analyst_manager_access_token`,
  `analyst_manager_proof_replay`) in one migration described as "registry
  analyst manager registrations"; audit events through `IAuditTrail`.
- Manager: Windows CNG persisted key (user scope, `ExportPolicy = None`) and one
  DPAPI-protected `registration.state` file in the Manager state directory.

**Testing**:

- Platform: xUnit v3, Shouldly, `WebApplicationFactory`
  (`Microsoft.AspNetCore.Mvc.Testing` 10.0.12), Testcontainers PostgreSQL,
  `FakeTimeProvider` (`Microsoft.Extensions.TimeProvider.Testing` 10.10.0) in
  `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Registry`; layer and
  visibility rules in `SocAlytics.Platform.Architecture.Tests`.
- Manager: xUnit v3 3.2.2, Shouldly 4.3.0, `FakeTimeProvider`, `FakeLogger`
  (`Microsoft.Extensions.Diagnostics.Testing` 10.10.0), `JsonSchema.Net` 8.0.5 (last MIT-licensed line, as in the platform contract tests)
  (test-only, validates local-control messages against the component-local
  schema) in `src/analysts/manager/Tests/SocAlytics.Analysts.Manager.Tests`;
  Windows-only adapter tests use `SkipUnless` and are skipped on Linux.

**Target Platform**: Platform API on any .NET 10 host (Linux OCI image in
deployment; Aspire AppHost locally). Manager registration on Windows 10
1803+/Windows Server 2019+ (x64, arm64); on Linux and macOS the Manager builds,
runs, reports status, and refuses registration fail-closed; CI runs on the Linux
runner.

**Project Type**: Web service (platform REST API) plus headless desktop worker
with a command-line control client.

**Performance Goals**: SC-001 (Active in under 5 minutes of hands-on time);
revocation observed by a connected Manager within one status-check interval
(non-production default 60 s, SC-003); restore diagnostics visible within 30 s
(SC-005); per Manager request at most one indexed token/registration lookup and
one replay insert.

**Constraints**: HTTPS only for Manager traffic (FR-015); platform clock decides
every expiry; no secret in logs, audit, status, diagnostics, or state files
(FR-034); no Avalonia UI, Analyst containers, models, NATS, object storage, or
production runtime integration; every configurable window and limit has an
explicit non-production default and no production approval.

**Scale/Scope**: One stamp with tens of Managers; 13 REST paths (14
operations), 3 tables, 1 migration; Manager with 1 production project and 1 test
project in its own solution.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

### Pre-design evaluation

| Gate | Result | Justification |
| --- | --- | --- |
| I. Architecture is the design authority | PASS | Follows [Analyst Manager](../../docs/architecture/analyst-manager.md#registration-and-stamp-binding) (two authorized actions, browser pairing, challenge activation, `private_key_jwt` + DPoP, revocation precedence, lifecycle and operating states), [Analyst Runtime and Recovery](../../docs/architecture/analyst-runtime-and-recovery.md#runtime-preflight-and-status) (preflight checks, compatibility list, built-in adapters only), [Platform Implementation](../../docs/architecture/platform-implementation.md#api-and-identity), [Security and Data Governance](../../docs/architecture/security-and-data-governance.md#audit-events), and [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md#representation-conventions). Refinements that the narratives do not yet state are listed under [Required Architecture Updates](#required-architecture-updates). Unresolved policy values stay explicit non-production defaults. |
| II. Respect source-area ownership | PASS | Registration records, authorization, credentials, and audit are in `src/platform` Registry folders inside existing layer projects (no new platform project). The Manager is in the planned `src/analysts/manager` path. No new first-level `src` child; directories are created only with their first artifacts. |
| III. API-first control plane | PASS | Every human action is an authorized REST operation; the Manager is an API consumer with no database access; no video bytes; OpenAPI 3.1 and JSON Schema 2020-12 contracts are authoritative, and the local-control schema lives with the Manager that validates it. |
| IV. Evidence over claims | PASS | Focused platform tests in `Integration.Tests/Registry` plus existing host and architecture tests; Manager tests in its own test project; all spec scenarios automated (SC-011). Windows-only adapters are tested on Windows and reported as skipped on Linux; nothing is claimed as deployment support or production readiness. |
| V. Focused, minimal changes | PASS | One grant, one client type, opaque tokens (no signing-key management), in-box rate limiting and sockets, no authorization-server framework, no UI framework, no runtime adapter, one Manager production project. Each added package is listed with its purpose. |
| Technology: deferred technologies | PASS | PostgreSQL/Dapper/DbUp come from the persistence dependency; human authentication from Club and Identity; machine authentication is adopted by this spec. NATS and S3 are not used. |
| Technology: environment features | PASS, dependent | `.github/actions/environment-verify` builds and tests only `src/platform/SocAlytics.Platform.slnx`; code under `src/analysts/manager/` would be unverified and reported as uncovered. This feature therefore depends on the combined environment feature [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md) (Manager scope below), which must be reviewed and merged first. The spec's Assumptions → Dependencies names it (coordinator update; this plan does not edit the spec). The platform part needs nothing beyond the current environment (SDK, Docker for Testcontainers). |
| Workflow and quality gates | PASS | The environment feature may change only the two action folders, so this feature owns every related documentation change (see [Documentation Updates](#documentation-updates)): the `README.md` composite-action description of `environment-verify`, the Manager commands in `README.md` and `AGENTS.md`, and the current-state text. Markdown passes `node .github/scripts/check-markdown.mjs`. No commits, pushes, or product CI workflows. |

### Required environment feature

- **Feature**: [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md), which combines the
  Manager build coverage proposed here with coverage of repository-root
  `contracts/`. It changes only `.github/actions/environment-verify` and, if
  needed, `.github/actions/environment-setup`.
- **Manager solution (confirmed)**:
  `src/analysts/manager/SocAlytics.Analysts.Manager.slnx`, containing
  `SocAlytics.Analysts.Manager/SocAlytics.Analysts.Manager.csproj` and
  `Tests/SocAlytics.Analysts.Manager.Tests/SocAlytics.Analysts.Manager.Tests.csproj`.
- **SDK pin (confirmed)**: the Manager has its own
  `src/analysts/manager/global.json` with content identical to
  `src/platform/global.json` (`10.0.400`, `rollForward: latestPatch`,
  `allowPrerelease: false`); it does not reference the platform file. Both pins
  must change together.
- **Verify**: add one block per the action's extension rules: define
  `MANAGER_SOLUTION=src/analysts/manager/SocAlytics.Analysts.Manager.slnx`; add
  `'^src/analysts/manager/'` to `COVERED` only when that file exists; when the
  scope changed or in finalize mode and the solution exists, run
  `dotnet restore`, `dotnet build --no-restore`, and
  `dotnet test --no-build` on it (check name "analyst manager build and tests");
  skip silently while the solution does not exist.
- **Setup**: no new tool. The setup action already installs the SDK from
  `src/platform/global.json`, which the identical Manager pin resolves to, so
  the Manager never needs a second SDK. No Docker or other service is needed by
  the Manager tests.
- **OS key-store test strategy**: the Linux runner runs every Manager test with
  the test-only key and state stores, the production fail-closed path of the
  unsupported store, and the Unix-socket control channel; Windows CNG and DPAPI
  adapter tests are skipped there with `SkipUnless` and run on Windows developer
  machines. No Windows runner is required (composite actions cannot change the
  runner OS); this coverage gap is an accepted risk.

### Post-design re-evaluation

| Gate | Result | Notes after Phase 1 |
| --- | --- | --- |
| I | PASS | Data model and contracts reproduce the architecture state machines exactly, including platform `Unregistering`; refinements are queued as Required Architecture Updates rather than silent divergence. |
| II | PASS | Final structure below keeps all platform files in Registry folders of existing projects and all Manager files under `src/analysts/manager`. No cross-area project references (each side tests against the contract). |
| III | PASS | 14 operations in [openapi.yaml](contracts/openapi.yaml) (validated as OpenAPI 3.1); human and machine authentication schemes are disjoint; local control defined in [local-control.schema.json](contracts/local-control.schema.json). |
| IV | PASS | [quickstart.md](quickstart.md) maps every user story and success criterion to an automated suite; the composed manual run is supplementary. |
| V | PASS | Final dependency set: platform +1 production package, +1 test package; Manager 5 production packages and 7 test packages (xUnit v3, its runner, test SDK, Shouldly, TimeProvider.Testing, Diagnostics.Testing, JsonSchema.Net). No Complexity Tracking entry is needed. |
| Environment | PASS, dependent | Unchanged: implementation starts only after [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md) is merged. |

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
│   ├── proof-profiles.md         # Activation proof, client assertion, DPoP profiles
│   ├── local-control.schema.json # Manager local-control messages (JSON Schema 2020-12)
│   └── local-control.md          # Transport, CLI surface, guarantees
└── tasks.md                      # Created by /speckit-tasks
```

### Source Code (repository root)

Platform part (modified and new files only; the existing scaffold is otherwise
unchanged):

```text
src/platform/
├── Directory.Packages.props                         # + JsonWebTokens, TimeProvider.Testing
├── SocAlytics.Platform.Domain/Registry/
│   ├── AnalystManagerRegistration.cs                # Lifecycle rules and transitions
│   ├── RegistrationState.cs
│   ├── DeviceKeyThumbprint.cs
│   ├── DeviceMetadata.cs
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
│   └── Persistence/Migrations/
│       └── NNNN_registry_analyst_manager_registration.sql
├── SocAlytics.Platform.Api/
│   ├── Program.cs                                   # Registry endpoints, DPoP scheme, rate limiter, worker
│   └── Registry/
│       ├── AnalystManagerPairingEndpoints.cs        # Stamp discovery, pairings, status, activation
│       ├── AnalystManagerTokenEndpoint.cs
│       ├── AnalystManagerSelfEndpoints.cs
│       ├── AnalystManagerRegistrationEndpoints.cs   # Human submission and decisions
│       ├── AnalystManagerDPoPAuthenticationHandler.cs
│       ├── RequireHttpsEndpointFilter.cs
│       ├── RegistryRateLimiting.cs
│       └── RegistrationExpiryWorker.cs
├── SocAlytics.Platform.AppHost/Program.cs           # Stamp__Id, Stamp__PublicBaseUri (HTTPS endpoint from Club and Identity)
└── Tests/
    ├── SocAlytics.Platform.Architecture.Tests/PlatformArchitectureTests.cs  # Registry visibility; anonymous allow list entries
    └── SocAlytics.Platform.Integration.Tests/Registry/
        ├── Support/TestAnalystManager.cs            # In-memory ECDSA proofs, DPoP, assertions
        ├── Unit/RegistrationStateMachineTests.cs
        ├── PairingTests.cs
        ├── DecisionTests.cs
        ├── ActivationTests.cs
        ├── TokenAndDPoPTests.cs
        ├── RevocationAndUnregistrationTests.cs
        ├── ExpiryTests.cs
        └── AuditAndSecretTests.cs
```

Analyst Manager part (all new except `src/analysts/README.md`):

```text
src/analysts/
├── README.md                                        # Updated: manager is executable
└── manager/
    ├── README.md                                    # Commands, supported stores, status
    ├── global.json                                  # Same SDK as src/platform/global.json
    ├── Directory.Build.props                        # net10.0, nullable, warnings as errors
    ├── Directory.Packages.props                     # Central package versions
    ├── SocAlytics.Analysts.Manager.slnx
    ├── contracts/local-control.schema.json          # Component-local executable contract
    ├── SocAlytics.Analysts.Manager/                 # Exe, assembly socalytics-manager
    │   ├── Program.cs                               # System.CommandLine root
    │   ├── Hosting/                                 # Host builder, options, DI
    │   ├── DeviceKeys/                              # IDeviceKeyStore, WindowsCngDeviceKeyStore,
    │   │                                            # UnsupportedDeviceKeyStore
    │   ├── LocalState/                              # IProtectedStateStore, WindowsDpapiStateStore,
    │   │                                            # UnsupportedStateStore, ProtectedRegistrationState
    │   ├── Platform/                                # PlatformClient, ProofFactory, token cache, signals
    │   ├── Registration/                            # RegistrationCoordinator, PairingSession,
    │   │                                            # RegistrationStatusMonitor
    │   ├── Operating/                               # OperatingStateMachine, AdmissionGate,
    │   │                                            # DrainCoordinator, IManagerWorkTracker
    │   ├── Runtime/                                 # IContainerRuntimeAdapter, RuntimeAdapterCatalog,
    │   │                                            # PreflightRunner, RuntimeCompatibilityList,
    │   │                                            # runtime-compatibility.json (embedded, empty)
    │   ├── LocalControl/                            # LocalControlServer, LocalControlClient, messages
    │   └── Diagnostics/                             # Status reporting, bounded diagnostic codes
    └── Tests/SocAlytics.Analysts.Manager.Tests/
        ├── Support/                                 # TestDeviceKeyStore, TestProtectedStateStore,
        │                                            # FakePlatform, SimulatedRuntimeAdapter,
        │                                            # SimulatedWork, ManagerTestHost
        ├── Registration/
        ├── Restore/
        ├── Revocation/
        ├── Preflight/
        ├── Operating/
        ├── LocalControl/
        ├── Secrets/
        └── Windows/                                 # CNG and DPAPI tests (SkipUnless Windows)
```

Repository documentation touched by implementation is listed in
[Documentation Updates](#documentation-updates).

**Structure Decision**: The platform part follows the layered monolith: one
`Registry` folder per layer in the existing Domain, Application,
Infrastructure, and Api projects, tests in the shared Integration and
Architecture test projects, and no new platform project. The Analyst Manager is
a separate .NET solution under the planned `src/analysts/manager` path with one
executable project (worker plus CLI) and one test project, because it belongs
to the analysts ownership area and must not join the platform solution. The two
solutions share no project references; they are tied together by
[contracts/openapi.yaml](contracts/openapi.yaml) and tested independently
against it.

## Implementation Notes

- **Authorization**: Registry Application commands call
  `IAccessAuthorizer.AuthorizeClubAsync` at execution time
  (`ClubPermission.Register` for submission; `ClubPermission.Administer` for
  approve, reject, revoke, list, and read). The authorizer records each denial
  once as `authorization.denied`; Registry adds no second event. Human
  endpoints are mapped with `MapMemberApi` (`SocAlyticsSession`, `ActiveMember`)
  and `SessionAntiforgeryFilter` (`X-CSRF-Token`). Manager endpoints are mapped
  outside that group: anonymous ones are added to the Club and Identity
  anonymous allow list, and `self` endpoints require the `AnalystManagerDPoP`
  scheme and scope `analyst-manager`. Neither family accepts the other's scheme
  (FR-013). Handlers return `OperationResult<T>`; the API maps failures with
  `ProblemResults` to `urn:socalytics:problem:<code>` problems.
- **Audit**: `IAuditTrail.RecordAsync` in the transition's unit of work, or
  `RecordIndependentAsync` for refusals that change nothing, writing
  `security_audit_event` with the `analyst-manager.*` event types, resource
  `analyst-manager-registration`, and the new `actor_kind` value
  `analyst-manager` (see [data-model.md](data-model.md#audit-events-security_audit_event)).
- **Concurrency**: lifecycle transitions use state guards
  (`WHERE id = @Id AND state = @ExpectedState`), never `If-Match`; reads return
  a strong ETag of `version`. A lost race on code submission returns the
  indistinguishable refusal; other lost races return `409`.
- **Expiry**: inline checks in every command plus `RegistrationExpiryWorker`,
  which also purges expired token and replay rows.
- **Secrets**: generated with `RandomNumberGenerator`, returned once, stored as
  SHA-256 hashes, never placed in log templates, exception messages, or problem
  details; tests assert their absence.
- **Manager startup order**: open the local-control socket, restore protected
  state (fail closed on any defect), obtain a token (assertion plus DPoP),
  confirm `self/registration`, run preflight, then enter the restored intent.
  Admission is possible only in `Running` after all steps succeeded in the
  current process.
- **Inactive signal**: the Manager clears stamp state and deletes the device key
  only after a proof-authenticated inactive response (research R17).

## Documentation Updates

Implementation tasks of this feature make these changes (constitution:
supported commands are documented in `README.md`, and `AGENTS.md` stays
current). The environment feature
[`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md)
changes only `.github/actions/environment-setup/` and
`.github/actions/environment-verify/`, so it cannot make them. Each change
describes behavior that exists once both features are merged.

1. **`README.md`, section "Requesting Implementation on GitHub", list "In this
   repository:" under the composite actions** (currently around lines
   222–228): replace the `environment-verify` item with:

   > `environment-verify` runs the platform restore, build, and tests when
   > `src/platform/**` changed, the Analyst Manager restore, build, and tests
   > (`src/analysts/manager/SocAlytics.Analysts.Manager.slnx`) when
   > `src/analysts/manager/**` changed, every check in finalize mode, and the
   > Markdown check when Markdown changed. It lists the paths it covers in
   > `COVERED`; changed files outside them are reported as not covered,
   > including platform or Manager files when their solution is missing.

   If the environment feature also adds repository-root `contracts/` coverage,
   its sentence is merged into the same item by whichever feature merges
   second; the Manager wording above is unchanged by that.

2. **`README.md`, section "Development"**: add a subsection after "Platform
   Host":

   > ### Analyst Manager
   >
   > The Analyst Manager is a separate .NET 10 solution that pins the same SDK
   > as the platform in its own `global.json`. Run these commands from the
   > repository root:
   >
   > ```powershell
   > dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
   > dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
   > dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
   > dotnet run --project src/analysts/manager/SocAlytics.Analysts.Manager -- run
   > ```
   >
   > The first slice is headless: `socalytics-manager register`, `status`,
   > `pause`, `resume`, `exit`, and `unregister` control a running Manager
   > through an owner-only local socket. Registration requires a supported
   > protected key store, which in this slice exists only on Windows; Linux and
   > macOS refuse registration. Windows-only key and state store tests are
   > skipped on other operating systems. No production container-runtime
   > integration ships, so a Manager stays Runtime unavailable. This is
   > development evidence, not production readiness.

3. **`README.md`, section "Repository Structure"**: change the `src/analysts/`
   item to "contains the executable headless Analyst Manager under
   `src/analysts/manager/` and records the planned Analyst SDK and Analyst
   capability ownership boundary", and change the paragraph after the list to
   say that the clients and agents source areas remain non-executable
   scaffolds while the platform and the Analyst Manager contain the
   repository's application projects and tests. In the "Platform Host"
   evidence paragraphs, add Analyst Manager registration (pairing, approval,
   activation, DPoP machine tokens, revocation) to the executable evidence and
   remove "identity and authentication" from the deferred list only as far as
   Club and Identity has not already done so.

4. **`AGENTS.md`**:
   - "Current State": the analysts area is executable for
     `src/analysts/manager`; add the Manager solution and its test project,
     and add Analyst Manager registration and machine authentication to the
     executable evidence (keeping the statement that no production-readiness
     evidence exists).
   - "Repository Setup": add the four Manager commands from item 2 after the
     platform commands, with the note that the Manager solution has its own
     `global.json` pinned to the platform SDK and that Windows-only store tests
     are skipped on other operating systems.

5. **`src/analysts/README.md`**: replace "Current Status" and the scaffold
   sentence in "Exclusions" so they state that `manager/` contains the
   executable headless Analyst Manager (no SDK or capabilities yet), and link
   `manager/README.md`.

6. **`src/analysts/manager/README.md`** (new): purpose, the commands from
   item 2, the local-control commands and exit codes from
   [contracts/local-control.md](contracts/local-control.md), supported key and
   state stores, non-production configuration defaults, and the readiness
   disclaimer.

7. **`src/platform/README.md`**: Registry endpoints, the `Stamp:Id` and
   `Stamp:PublicBaseUri` configuration, and the Registry option defaults.

## Required Architecture Updates

The coordinator applies these; this plan does not edit `docs/architecture/`.

1. **`docs/architecture/analyst-manager.md`, section "Desktop Application
   Profile"**, append after the paragraph that begins "The provisional desktop
   implementation uses a .NET 10 Generic Host":

   > The worker exposes its local operating controls (status, register, pause,
   > resume, safe exit, and unregister) to a command-line client through an
   > owner-only local IPC endpoint: a Unix domain socket in the Manager state
   > directory on every operating system. The endpoint accepts connections only
   > from the Manager's process account and never listens on the network. The
   > headless worker and its command-line client are the first implemented
   > control surface; the tray UI, when introduced, uses the same controls.

2. **`docs/architecture/analyst-manager.md`, section "Browser Pairing And
   Approval"**, append after the sentence "Hardware-backed storage may be used
   but is not required.":

   > The first implemented candidate store is a non-exportable ECDSA P-256 key
   > in the Windows CNG software key storage provider, scoped to the Manager's
   > process account, with the local registration state protected by Windows
   > DPAPI for the same account. Until another operating system has an approved
   > store, the Manager refuses registration there. Production approval of any
   > store remains governed by `GOV-CRED-002` in Security and Data Governance
   > (link target `security-and-data-governance.md`).

3. **`docs/architecture/analyst-manager.md`, section "Machine
   Authentication"**, append after the sentence "AM tokens cannot authorize
   human or administrative APIs.":

   > The DPoP proof key is the registered device key, so every token request and
   > API call proves possession of the non-exportable key. Access tokens are
   > opaque references stored only as hashes and validated against the
   > authoritative registration on every call, so revocation takes effect without
   > a platform token-signing key. The AM treats its registration as inactive
   > only after a refusal that follows its own valid key proof; transport
   > failures never clear its protected registration.

4. **`docs/architecture/platform-implementation.md`, section "API And
   Identity"**, append after the paragraph that begins "An active AM uses OAuth
   2.0 client credentials":

   > The Analyst Manager token endpoint is a minimal endpoint in the API's
   > Registry functional area that supports only this grant; the platform does
   > not run a general-purpose authorization server for machine clients.

5. **`docs/architecture/platform-implementation.md`, section "Analyst
   Technology"**, append to the first paragraph:

   > Its source is a separate .NET solution under `src/analysts/manager` with its
   > own tests. The first implemented slice is headless: it registers, restores,
   > observes revocation, runs runtime preflight, and accepts local controls
   > without the Avalonia UI.

6. **`docs/architecture/platform-implementation.md`, section "Source And
   Runtime Baseline"**, replace the `src/analysts` bullet with:

   > `src/analysts` owns the Analyst Manager, whose first headless slice is
   > executable under `src/analysts/manager`, and the future Analyst SDK and
   > Analyst capability implementations across their required runtimes

## Dependencies and Coordination

- **Environment**: [`20261007-115855-environment-verification-coverage`](../20261007-115855-environment-verification-coverage/spec.md) must be merged first; the coordinator records it
  in `spec.md` Assumptions → Dependencies.
- **Platform Persistence Foundation**: `IUnitOfWork`, migrations and Migrator,
  `socalytics.advance_version()`, structural test, `Integration.Tests` project.
- **Club and Identity Foundation**: the shared names listed under Primary
  Dependencies; `member_account(id)` as the foreign-key target of
  `submitted_by`, `decided_by`, and `revoked_by`; the `club` table for the club
  name in Manager status; the AppHost HTTPS endpoint; and the anonymous allow
  list in the architecture tests.
- **Coordination items for Club and Identity** (resolve at `/speckit-tasks` or
  `/speckit-analyze` without changing behavior designed here):
  - `IAccessAuthorizer.AuthorizeClubAsync` should accept an optional
    `AuditResource` so the `authorization.denied` event names the registration
    (FR-033 requires the request to be identifiable in denial events). Fallback:
    Registry records the registration id in the request correlation only.
  - `OperationFailure` needs a `400` failure that carries a problem code (for
    the indistinguishable `analyst-manager-pairing-code-invalid` and
    `analyst-manager-polling-handle-invalid` refusals and
    `analyst-manager-activation-proof-invalid`); proposal: `Validation` with an
    optional top-level code.
  - The `security_audit_event` `actor_kind` check gains `analyst-manager`, and
    `resource_type` gains `analyst-manager-registration` if constrained; the
    Registry migration applies this.
- **AppHost**: this feature adds only the stamp configuration (`Stamp__Id`,
  `Stamp__PublicBaseUri` from the API HTTPS endpoint).

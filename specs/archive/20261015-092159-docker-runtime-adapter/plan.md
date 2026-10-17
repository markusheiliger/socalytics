# Implementation Plan: Docker Desktop Runtime Adapter for Analyst Execution

**Branch**: `20261015-092159-docker-runtime-adapter` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261015-092159-docker-runtime-adapter/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

The Analyst Manager gains its first real runtime adapter: the built-in
`docker-desktop` profile behind the existing `IContainerRuntimeAdapter` seam.
A thin, Manager-owned Engine API client talks only to a local endpoint (the
Windows named pipe; the Linux socket only in validation configurations). The
preflight maps the runtime's `/version` and `/info` to the existing
`RuntimeProbe` and decides support only through the compatibility list.
Attempts pull the pinned image by digest with short-lived pull credentials the
platform issues, run one restricted, network-less container with the job,
input, result, and scratch mounts of the
[Analyst container interface](contracts/analyst-container.md), map the exit to
the attempt-completion categories, keep logs on the host, and clean up.
Startup and periodic reconciliation touch only containers labelled with the
Manager's identity, and an image cache with a disk budget keeps hosts tidy.
The platform adds two Manager self endpoints: registry pull credentials scoped
to the default workflow's repositories, and the default workflow's capability
requirements, from which the Manager computes its offer. A test Analyst image
proves the path end to end in continuous integration; Docker Desktop on
Windows is verified manually, recorded in the architecture with the open gap
of automated Windows verification.

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`
and `src/analysts/manager/global.json`); Python 3 only inside the test Analyst
image.

**Primary Dependencies**: Existing only: ASP.NET Core minimal APIs, Dapper and
PostgreSQL, NATS.Net, the platform's `OciDistributionClient`, Avalonia and
System.CommandLine in the Manager. The Engine API client is built on
`HttpClient` (research R1). Tests: xUnit v3, Shouldly, Testcontainers (already
in the platform tests; its Docker client builds and pushes the test image).

**Storage**: No new platform table. Manager: one new local file
`image-cache.json` in its state directory (data-model.md); per-attempt host
directories under the attempt root.

**Testing**: Manager unit and component tests with a fake Engine API
`HttpMessageHandler`; platform Integration tests (`registry:2` fixture plus a
token-auth registry fake) for the two endpoints; Contracts tests for two new
schema families; one Docker-backed end-to-end test (`DockerRuntimeEndToEndTests`)
running the real Manager core.

**Target Platform**: Analyst Manager on Windows with Docker Desktop (WSL2,
Linux containers, CPU). Automated verification on Linux through the local
Docker socket in validation configurations.

**Project Type**: Desktop/tray application plus web service (existing
Manager and platform solutions).

**Performance Goals**: No new targets; a cached image starts an attempt
without a pull, and heartbeats continue during pulls (FR-011).

**Constraints**: Local endpoint only; no credentials or network in Analyst
containers; logs never leave the host; only sanitized completion categories;
platform API-first (constitution III); production compatibility values and
credential lifetimes stay configurable (`GOV-CRED-004` unresolved).

**Scale/Scope**: One Manager per host; attempts bounded by the existing
effective concurrency limit and the configured CPU and memory budget.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Status | Evidence |
| --- | --- | --- |
| I. Architecture is the design authority | Pass | Implements `analyst-runtime-and-recovery.md` (OCI boundary, adapter profiles, preflight, labelling and reconciliation). New design (platform-issued registry pull credentials, the Analyst container interface, Windows verification status and gap) reaches the architecture through `## Architecture Updates` and the documentation task. |
| II. Source-area ownership | Pass | Adapter, cache, reconciliation, and status in `src/analysts/manager`; endpoints, application handlers, and registry token exchange in `src/platform`; the test Analyst image with the end-to-end tests in `src/platform/Tests`. No new first-level area. |
| III. API-first control plane | Pass | Both Manager interactions are versioned REST endpoints with JSON Schema families in `contracts/` and OpenAPI snapshot entries; the control plane serves metadata only (no image bytes pass through it). |
| IV. Evidence over claims | Pass | Each requirement maps to Manager, Integration, Contracts, or end-to-end tests (quickstart.md). Windows support is recorded as manually verified, not production-ready. |
| V. Focused, minimal changes | Pass | No new production package; Testcontainers is already a platform test dependency. |
| Tooling | Pass | The environment actions already provide the .NET SDK, Docker, and SoftHSM2 and run both solutions' tests; no environment feature is needed (research R12). |

Re-check after Phase 1 design: still passes; no Complexity Tracking entries.

## Architecture Updates

### Narratives

- [Analyst Runtime and Recovery](../../../docs/architecture/analyst-runtime-and-recovery.md):
  OCI Container Runtime Boundary, Built-In Runtime Adapter Profiles, Runtime
  Preflight And Status (launch defaults and the Analyst container interface),
  Registration And Runtime Credentials (registry pull credentials, capability
  offer, labelling and reconciliation), and Analyst Manager Responsibilities —
  implemented by this feature.
- [Analyst Manager](../../../docs/architecture/analyst-manager.md): the opening
  description of the built-in OCI Runtime Adapter, Application Profiles (the
  Core worker's OCI adapters), and the Tray Profile status window (offered
  capabilities, image cache use, reconciliation state).
- [Platform Implementation](../../../docs/architecture/platform-implementation.md):
  Analyst Technology, Acceptance Evidence, and Architecture Reassessment, which
  record what is implemented and evidenced.
- [Analysts, Models, and Hardware](../../../docs/architecture/analysts-models-and-hardware.md):
  Implementation Validation (the Windows runtime spike and security tests) and
  the result-acceptance paragraph that names the simulated Analyst.
- [Security and Data Governance](../../../docs/architecture/security-and-data-governance.md):
  `CTL-004` (non-root, constrained Analyst execution), whose evidence this
  feature provides.
- `contracts/README.md` (outside `docs/architecture/`, kept current with the
  contract task): the contract family table gains the two new families
  `analyst-manager/registry-credentials/v1` and
  `analyst-manager/workflow-capabilities/v1`.
- [Tenancy and Technology](../../../docs/architecture/tenancy-and-technology.md):
  Execution Plane ("Docker Desktop first to validate").

### Design updates

Applied now to `analyst-runtime-and-recovery.md`, each marked "decided, not yet
implemented"; the implementation turns each into present tense:

1. **Verification of the Docker Desktop profile** (Built-In Runtime Adapter
   Profiles): automated verification through the Linux engine's local socket,
   accepted only in Development and Test; manual verification of Docker
   Desktop on Windows; only manually verified versions enter the shipped
   compatibility matrix; automated Windows-runner verification is an open gap
   outside the Spec Kit flow.
2. **Analyst container interface** (after the launch defaults): job, input,
   result, and scratch mount paths, small `/tmp`, networking disabled, no
   environment variables, Manager/job/attempt labels, logs kept on the host and
   only the sanitized failure category sent to the platform.
3. **Registry pull credentials** (Registration And Runtime Credentials):
   short-lived, read-only, platform-issued through the token service, limited
   to the default workflow's repositories inside the stamp's registry prefix
   (GHCR namespace in production), memory-only on the Manager.
4. **Capability offer** (Registration And Runtime Credentials): the Manager
   obtains the default workflow's capability requirements and offers those
   that fit, narrowed by an optional allowlist.
5. **Image cache** (Analyst Manager Responsibilities): a disk budget and an
   unused period; images the Manager did not pull are never removed.

The remaining design (thin Engine API client, endpoint schemes, attempt
directory layout, configuration keys) is implementation detail recorded in
[research.md](research.md) and [data-model.md](data-model.md).

### Status statements at merge

- `platform-implementation.md`, Analyst Technology: "Manager work execution is
  verified against a simulated runtime and Analyst that exist only in
  Development and Test; real Analyst execution remains deferred." → real
  execution on the Docker Desktop profile is implemented and verified end to
  end against a Linux engine, with Docker Desktop on Windows awaiting (or
  carrying) manual evidence; the simulated runtime remains for Development and
  Test.
- `platform-implementation.md`, Overview (line 15): "Analyst execution, external
  OpenID Connect sign-in, MFA enforcement, … do not exist yet." → Analyst
  execution on the Docker Desktop profile exists; the others still do not.
- `platform-implementation.md`, Acceptance Evidence: the list of evidenced tests
  gains the Docker-backed end-to-end execution test; "registry transport
  mapping, runtime conformance, …" under validation targets loses the parts this
  feature evidences.
- `platform-implementation.md`, Architecture Reassessment: "workflow failure
  behavior with real Analyst execution remain unevidenced", "against a
  simulated Analyst are implemented", and "the Avalonia/OCI platform matrix
  remain unsupported by implementation evidence" → state the evidenced Linux
  engine path and the still-missing Windows automation and broader matrix.
- `analysts-models-and-hardware.md`, result acceptance: "implemented as
  development evidence against the simulated Analyst" → also against a real
  Analyst container.
- `analysts-models-and-hardware.md`, Implementation Validation: "also requires
  a Windows runtime spike covering Docker Desktop and Podman profile APIs, local
  endpoint authorization, … orphan reconciliation, … image pull and launch
  latency. Security tests must reject forbidden mounts, devices, privileges,
  host namespaces, and runtime endpoint exposure." → record which parts this
  feature evidences (Docker Desktop API path, endpoint policy, reconciliation,
  launch restrictions) and which remain (Podman, Windows automation, latency).
- `analyst-runtime-and-recovery.md`, Analyst Manager Responsibilities: "obtain
  and rotate short-lived API and NATS credentials" → also registry pull
  credentials; "combine Analyst type, requested model, and host capabilities to
  select a runtime-tagged image" and "pull the image and resolve its immutable
  OCI digest" contradict the pinned digest the platform assigns → the AM pulls
  the job's pinned image digest and never selects an image.
- `analyst-manager.md`, Tray Profile status window list: add the offered
  capabilities with reasons for those not offered, the image cache use, and the
  reconciliation state.
- `security-and-data-governance.md`, `CTL-004` "Provisional" → evidence exists
  for the Docker Desktop profile (keep the status unless governance promotes
  it).
- `tenancy-and-technology.md`, Execution Plane: "Externally managed runtime
  profiles (Docker Desktop first to validate)" → Docker Desktop implemented,
  awaiting manual Windows evidence.

The documentation task in `tasks.md` must make every narrative above and every
status statement the feature affects anywhere in `docs/architecture/` true for
the merged state, re-reading the documents at implementation time; the
rewrites listed here are examples, not the whole job.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261015-092159-docker-runtime-adapter/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── analyst-container.md
│   ├── registry-credentials.md
│   └── workflow-capabilities.md
├── checklists/
│   └── requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
contracts/analyst-manager/
├── registry-credentials/v1/        # new schema family, examples, release snapshot
└── workflow-capabilities/v1/       # new schema family, examples, release snapshot

src/platform/
├── SocAlytics.Platform.Api/
│   ├── Registry/AnalystManagerSelfEndpoints.cs        # + registry-credentials, workflow-capabilities
│   └── openapi/v1.json                                 # snapshot gains both operations
├── SocAlytics.Platform.Application/Registry/
│   ├── RegistryCredentials/IssueRegistryCredentials.cs # handler, command, result
│   └── WorkflowCapabilities/GetWorkflowCapabilities.cs # handler, query, result
├── SocAlytics.Platform.Infrastructure/Registry/Oci/
│   ├── ImageRegistryOptions.cs                         # + PullCredentialLifetime
│   └── OciPullTokenIssuer.cs                           # token-service exchange (reuses OciDistributionClient auth)
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Registry/ # endpoint tests, token-auth registry fake
    ├── SocAlytics.Platform.Contracts.Tests/            # new families validated
    └── SocAlytics.Platform.EndToEnd.Tests/
        ├── DockerRuntimeEndToEndTests.cs
        ├── Support/EndToEndHarness.cs                  # docker-desktop Manager variant, registry fixture
        └── TestAnalyst/                                 # Dockerfile, analyst.py, analyst-manifest.json

src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/
│   ├── Runtime/Docker/
│   │   ├── DockerEngineClient.cs                       # HttpClient over npipe/unix (R1)
│   │   ├── DockerEndpointPolicy.cs                     # R2
│   │   ├── DockerRuntimeAdapter.cs                     # IContainerRuntimeAdapter, probe, offer (R3, R5)
│   │   ├── DockerAttemptExecution.cs                   # IAttemptExecution, launch, outcome (R6–R8)
│   │   ├── DockerReconciliationService.cs              # R9
│   │   ├── ImageCache.cs                               # R10, image-cache.json
│   │   └── DockerRuntimeOptions.cs                     # Runtime:Docker section
│   ├── Platform/PlatformClient.cs, PlatformWorkContracts.cs # + both endpoints
│   ├── Runtime/RuntimeCompatibilityList.cs             # + Development-only local list
│   ├── Work/AttemptRunner.cs, ResultPublisher.cs       # attempt directory layout, job.json
│   └── Hosting/ManagerServiceCollectionExtensions.cs   # register the adapter and services
├── SocAlytics.Analysts.Manager/                         # status window and CLI: offer, cache, reconciliation
└── Tests/SocAlytics.Analysts.Manager.Tests/Runtime/Docker/ # fake Engine API tests
```

**Structure Decision**: The adapter is a folder in the existing Manager Core
project behind the existing seam, since the architecture requires built-in,
versioned adapters shipped with the Manager; the platform additions follow the
queue-credential endpoint pattern across Api, Application, and Infrastructure.

## Dependencies and Sequencing

- Analysis Result Read for Jobs is merged: `UpstreamStager` stages the inputs
  into `UpstreamStager.AttemptsRoot/{attempt}/inputs`, and
  `AttemptLaunch.InputsDirectory` names that directory. This feature binds it
  read-only when present and makes `UpstreamStager.AttemptsRoot/{attempt}/` the
  single attempt directory: `job.json`, `inputs/`, `result/`, and `scratch/`
  (data-model.md), moving the result directory there from
  `socalytics-analyst-results`.
- The `docker-desktop` compatibility entry ships only after a person's manual
  Windows verification in a separate change (quickstart.md § Windows).

## Complexity Tracking

No constitution violations.

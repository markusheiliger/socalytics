# Implementation Plan: Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI

**Branch**: `20261018-092503-oci-images` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261018-092503-oci-images/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Add one reproducible, multi-stage image definition per deployable: the platform API and the Migrator from
`src/platform`, and the Web UI from `src/clients/web`. Base images are pinned by digest; the .NET images use chiseled
runtime bases and run as UID 1654, the Web UI image uses the unprivileged nginx base as UID 101, and all three run on a
read-only root filesystem with `/tmp` as the only writable location. Each build requires a source revision and one
release version (`YYYY.MMDD.HHMMSS`) and writes them to OCI labels. The Web UI image serves the built PWA byte for byte
with the production security headers and itself forwards `/api` and `/alive` to the API container, giving every
profile one origin from two images. A new Docker-backed test project builds the three images with the documented
commands and proves the API's alive and healthy states in its production environment, the absence of a migration path
in the API image, the Migrator's outcomes, the Web UI serving, integrity, header, and forwarding rules, non-root
read-only execution, and the absence of secret material in every layer (see [research.md](research.md)).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`, 10.0.4xx); TypeScript 5.9 on Node.js 24
for the Web UI; Dockerfile syntax built by the Docker CLI with BuildKit; nginx configuration for the Web UI server.

**Primary Dependencies**: Existing projects `SocAlytics.Platform.Api`, `SocAlytics.Platform.Migrator`, and
`src/clients/web`; base images `mcr.microsoft.com/dotnet/sdk` (exact 10.0.4xx tag), `dotnet/aspnet:10.0-noble-chiseled-extra`,
`dotnet/runtime:10.0-noble-chiseled-extra`, `node:24-bookworm-slim`, and `nginxinc/nginx-unprivileged` (Alpine), all
pinned by digest; Kiota 1.36.0 inside the Web UI build; Testcontainers 4.15.0, xUnit v3, and Shouldly for the evidence.
No new NuGet or npm package.

**Storage**: N/A (no table or migration; the evidence uses disposable PostgreSQL, NATS, and RustFS containers).

**Testing**: New `src/platform/Tests/SocAlytics.Platform.Images.Tests` (Docker-backed); a Vitest unit test in
`src/clients/web` that keeps the server template's Content Security Policy equal to the policy module; existing
Host, Architecture, Integration, and Web UI tests stay green.

**Target Platform**: Linux OCI containers on any OCI runtime; evidence on the build host's CPU architecture.

**Project Type**: Container packaging of an existing web service, one-shot console tool, and static web client.

**Performance Goals**: API alive within 30 s and healthy within 60 s of start (SC-002); image builds fit the existing
verification run (each image is built once per test run).

**Constraints**: Non-root, read-only root filesystem, no secrets or development or test material in any layer, no
profile-specific dependency, no in-container health probe, no change to `.github/` or the AppHost composition.

**Scale/Scope**: Three image definitions with ignore files, one nginx configuration and template, one npm script split,
one publish exclusion, one test project, and documentation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements the image parts of [platform implementation](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline), [client delivery](../../docs/architecture/client-applications.md#source-ownership-and-delivery), and the [portability rules](../../docs/architecture/production-operations.md#azure-container-apps-target-profile-provisional); the design choices it adds are recorded under Architecture Updates; production values stay Open / Blocking. | Pass |
| II. Source-area ownership | API and Migrator definitions in `src/platform`, Web UI definition in `src/clients/web`; no new `src/` child; tests in `src/platform/Tests`. | Pass |
| III. API-first control plane | The Web UI image forwards API paths without business logic; video bytes keep going directly to object storage. | Pass |
| IV. Evidence over claims | Docker-backed tests prove every requirement; images are not presented as deployment support or production readiness. | Pass |
| V. Focused, minimal changes | No new package; only the npm script split and a publish exclusion touch existing projects. | Pass |
| Environment features | Needs .NET 10, Node.js 24, and Docker with `docker build`; all provided by `environment-setup` and the runner (research R9). No environment spec. | Pass |

Post-design re-check: Pass; no violation needs Complexity Tracking.

## Architecture Updates

### Narratives

- [Platform Implementation — Source And Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline):
  the cloud-neutral OCI images, the Migrator as its own image, and the API image without a migration path.
- [Client Applications — Source Ownership And Delivery](../../docs/architecture/client-applications.md#source-ownership-and-delivery):
  the Web UI as a separate OCI container from `src/clients`, release-coupled to the API, on one origin.
- [Platform Implementation — Client Technology](../../docs/architecture/platform-implementation.md#client-technology):
  the separately containerized Web UI and its production server configuration.
- [Production Deployment and Operations — Azure Container Apps Target Profile](../../docs/architecture/production-operations.md#azure-container-apps-target-profile-provisional):
  the portability rules (both profiles run the same images).
- [Security and Data Governance](../../docs/architecture/security-and-data-governance.md): the Web UI Content Security
  Policy and its production server configuration.
- [Architecture Overview — Implementation Profile](../../docs/architecture/overview.md#implementation-profile): the
  production artifacts as cloud-neutral OCI images.

### Design updates (applied now, worded as decided but not implemented)

- `platform-implementation.md`, Source And Runtime Baseline: new paragraph after "Future deployables will use
  cloud-neutral OCI images …" that records the three Dockerfiles and their allow-list ignore files, digest-pinned
  chiseled .NET and unprivileged nginx bases, UIDs 1654 and 101, the read-only root with `/tmp`, configuration through
  environment variables, no in-container health probe, the required revision and `YYYY.MMDD.HHMMSS` release version in
  OCI labels, the API image without Migrator or DbUp, and the Web UI image's own forwarding of `/api` and `/alive`. The
  implementation must turn it into present tense.
- `client-applications.md`, Source Ownership And Delivery: new paragraph on the Web UI image (byte-for-byte shell,
  one policy module with the object storage origin as the only runtime part, revalidation and caching rules, client
  routes and not found, forwarding with streaming and 502). The implementation must turn it into present tense.
- `production-operations.md`, portability rules: new bullet on the shared deployable contract of the platform's images.
  The implementation must drop "decided, not yet implemented".

### Status statements at merge

- `platform-implementation.md`, Source And Runtime Baseline: "It will ship as its own OCI image and will be the only
  component … the API image will contain no migration execution path." → present tense: the Migrator ships as its own
  image and the API image contains no migration execution path.
- `platform-implementation.md`, Source And Runtime Baseline: "Future deployables will use cloud-neutral OCI images." →
  the platform API, Migrator, and Web UI are cloud-neutral OCI images; Analyst images and Hermes keep their own status.
- `platform-implementation.md`, Source And Runtime Baseline: "It will compose the platform API and Web UI as separate
  OCI containers: the API container will be built from the platform source area, while the Web UI container will be
  built from the client source area …" → the containers' images exist and are built from those areas; only the Compose
  composition itself stays future.
- `platform-implementation.md`, Client Technology: "The service worker is implemented as development evidence; offline
  mode, the container image, and production server configuration are not implemented." → the container image and its
  production server configuration are implemented; offline mode is not.
- `platform-implementation.md`, Architecture Reassessment ("Prototype, measurement, or implementation evidence supports
  the choice"): add the three images and their container evidence to the implemented evidence without claiming
  deployment support or production readiness.
- `client-applications.md`, Source Ownership And Delivery: "The Web UI release … is intended to be built and deployed as
  a separate OCI container from `src/clients`" and "offline mode, the OCI container image, and Docker Compose
  deployment are not implemented yet" → the image is built from `src/clients`; offline mode and Compose deployment are
  not implemented.
- `overview.md`, Implementation Profile: "production artifacts will be cloud-neutral OCI images" → the platform API,
  Migrator, and Web UI images exist as cloud-neutral OCI images; production readiness stays unresolved.
- `security-and-data-governance.md`, Web UI policy paragraph: "The production server configuration is not implemented
  and remains deferred." → the Web UI image applies the same policy; the production storage origin stays unresolved.

The documentation task in `tasks.md` must make these narratives and every status statement the feature affects true
for the merged state: it re-reads `docs/architecture/` at implementation time (including summaries, introductions, and
evidence lists, not only the sections above) and corrects every statement the merged images make false, using the
rewrites listed here as examples rather than the whole job, while keeping production profile values, registries,
publication, Compose, and Azure Container Apps explicitly unresolved or future.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-092503-oci-images/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── container-images.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Api/
│   ├── Dockerfile                     # new: API image
│   ├── Dockerfile.dockerignore        # new: allow-list context for the API image
│   └── SocAlytics.Platform.Api.csproj # changed: appsettings.Development.json not published
├── SocAlytics.Platform.Migrator/
│   ├── Dockerfile                     # new: Migrator image
│   └── Dockerfile.dockerignore        # new
├── SocAlytics.Platform.slnx           # changed: adds the Images test project
├── README.md                          # changed: image build commands and runtime contract summary
└── Tests/
    └── SocAlytics.Platform.Images.Tests/   # new: Docker-backed image evidence
        ├── SocAlytics.Platform.Images.Tests.csproj
        ├── Support/                    # image build fixture, shared network and dependencies, layer scanner
        ├── ApiImageTests.cs
        ├── MigratorImageTests.cs
        ├── WebUiImageTests.cs
        └── ImageHardeningTests.cs      # users, read-only root, labels, no health probe, no secret material

src/clients/web/
├── Dockerfile                          # new: Web UI image
├── Dockerfile.dockerignore             # new
├── container/
│   ├── nginx.conf                      # new: unprivileged, /tmp paths, no default server
│   └── default.conf.template           # new: static rules, headers, one-origin forwarding
├── build/securityHeaders.ts            # new: single source of the production policy and common headers
├── vite.config.ts                      # changed: imports the policy module
├── package.json                        # changed: build = generate + build:bundle
└── tests/unit/build/securityHeaders.test.ts  # new: server template policy equals the module's policy

README.md                               # changed: image build commands under Development
src/clients/README.md                   # changed: Web UI image commands and status
AGENTS.md                               # changed: Current State and Repository Setup commands
docs/architecture/                      # changed: see Architecture Updates
```

**Structure Decision**: Each image definition lives with the project it packages, inside the existing covered source
scopes. The evidence is one new platform test project that reuses the Integration test fixtures' configuration
helpers, like the existing EndToEnd test project.

## Complexity Tracking

No violations.

# Implementation Plan: Compose Stamp Composition for Self-Hosted Evaluation

**Branch**: `20261018-094332-compose-stamp` | **Date**: 2026-10-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261018-094332-compose-stamp/spec.md`

**Note**: This template is filled in by the `/speckit-plan` command; its definition describes the execution workflow.

## Summary

Add one committed Compose definition for one evaluation stamp under `src/platform/deploy/compose/`: PostgreSQL,
NATS JetStream, and RustFS pinned by digest with health checks and named volumes; the Migrator as a one-shot service
that the API waits for with `service_completed_successfully`; the API in its production environment with NATS JWT
authentication, the application database role, and an operator-supplied SMTP relay; and the Web UI as the only
published service, on the host's loopback interface, forwarding the API on one origin. Four project-scoped networks
keep the data services internal and make the API's egress explicit. A new setup console
(`SocAlytics.Platform.StampSetup`) writes every secret and the NATS trust material into a new stamp directory outside
the repository, from a committed environment file template with no secret defaults. A new Docker-backed test project
drives `docker compose` with disposable stamps and proves the order, ingress, readiness, sign-in through the origin,
restart, isolation of a second project, and leak-free teardown (see [research.md](research.md)).

## Technical Context

**Language/Version**: C# on .NET 10 (SDK pinned by `src/platform/global.json`); Compose Specification files run by the
Docker CLI's Compose v2 plugin; POSIX shell only inside container health checks.

**Primary Dependencies**: The API, Migrator, and Web UI images of the batch sibling
[`specs/archive/20261018-092503-oci-images`](../archive/20261018-092503-oci-images/contracts/container-images.md); infrastructure images
`postgres:18`, `nats:2.14-alpine`, and `rustfs/rustfs:1.0.1`, each pinned by digest; the existing roles script
`src/platform/SocAlytics.Platform.AppHost/PostgresInit/01-socalytics-roles.sh`; `NATS.Jwt` 1.0.1 and `NATS.NKeys` 1.0.2
(already pinned); xUnit v3 and Shouldly for the evidence. No new package.

**Storage**: Stamp-scoped named volumes for PostgreSQL, JetStream, and object storage; no table or migration.

**Testing**: New `src/platform/Tests/SocAlytics.Platform.Compose.Tests` (Docker with Compose); unit tests of the setup
console in the same project; existing Host, Architecture, Integration, and Images tests stay green.

**Target Platform**: One Linux container host with Docker Engine and the Compose v2 plugin; evidence on the build host's
CPU architecture.

**Project Type**: Deployment composition plus a small setup console for an existing web service, one-shot tool, and
static web client.

**Performance Goals**: Stamp reaches the sign-in page within 5 minutes with images present (SC-001); API alive within 3
minutes of the start command and healthy within 1 minute after a default workflow exists (SC-002).

**Constraints**: Production hosting environment and its checks; images run per their contract (non-root, read-only
root, `/tmp`); only the Web UI published, on loopback; no secrets or trust material in the repository; no
`container_name` or fixed resource names; no change to `.github/`, the AppHost composition, or the images.

**Scale/Scope**: One Compose file, one template, one README, one setup console project, one test project with an
evidence-only override file, and documentation.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements the Compose parts of [platform implementation](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline), the [initial profile](../../docs/architecture/production-operations.md#versioned-production-profile) and [invariants](../../docs/architecture/production-operations.md#scope-and-invariants), and [tenancy](../../docs/architecture/tenancy-and-technology.md#initial-production-profile); the evaluation choices it adds are recorded under Architecture Updates; profile values stay Open / Blocking. | Pass |
| II. Source-area ownership | Composition, template, and setup console in `src/platform/`; tests in `src/platform/Tests`; no new `src/` child. | Pass |
| III. API-first control plane | No business logic outside the API; video bytes never pass through the API or the Web UI (uploads are unavailable rather than proxied). | Pass |
| IV. Evidence over claims | Docker-backed tests prove every requirement; the documentation states that the composition is not a production profile and passes no preflight. | Pass |
| V. Focused, minimal changes | No new package; one small console for the trust material that shell tools cannot produce; no platform behavior change. | Pass |
| Environment features | Needs .NET 10 and Docker with the Compose v2 plugin; the SDK comes from `environment-setup`, Docker and Compose from the runner (research R11). No environment spec. Depends on `specs/archive/20261018-092503-oci-images` for the images. | Pass |

Post-design re-check: Pass; no violation needs Complexity Tracking.

## Architecture Updates

### Narratives

- [Platform Implementation — Source And Runtime Baseline](../../docs/architecture/platform-implementation.md#source-and-runtime-baseline):
  the Compose composition of the platform API and Web UI, PostgreSQL, NATS JetStream, S3-compatible storage, and the
  one-shot Migrator.
- [Production Deployment and Operations — Versioned Production Profile](../../docs/architecture/production-operations.md#versioned-production-profile)
  and [Scope and Invariants](../../docs/architecture/production-operations.md#scope-and-invariants): one project
  namespace per stamp, internal networks, declared ingress and egress, the Migrator's
  `service_completed_successfully` ordering, and the profile values that stay Open / Blocking.
- [Production Deployment and Operations — Validation and Promotion Evidence](../../docs/architecture/production-operations.md#validation-and-promotion-evidence):
  the evaluation composition is not "a real Compose deployment" for production acceptance.
- [Deployment and Technology — Initial Production Profile](../../docs/architecture/tenancy-and-technology.md#initial-production-profile):
  the Compose project namespace as one stamp on stamp-dedicated volumes and networks.
- [Client Applications — Source Ownership And Delivery](../../docs/architecture/client-applications.md#source-ownership-and-delivery):
  "Docker Compose deployment are not implemented yet".

### Design updates (applied now, worded as decided but not implemented)

- `platform-implementation.md`, Source And Runtime Baseline: new paragraph after the Compose paragraph that records the
  evaluation composition `src/platform/deploy/compose/compose.yaml` (decided, not yet implemented): image references
  as required values, digest-pinned infrastructure images with health checks, the Migrator one-shot and the API's
  `service_completed_successfully` dependency, the four project-scoped networks with only the Web UI published on the
  host's loopback interface, the API in its production environment with NATS JWT authentication and the existing roles
  script, the bucket created by the API's existing option, the setup console `SocAlytics.Platform.StampSetup` that
  writes secrets and NATS trust material into a stamp directory outside the repository, readiness unchanged (healthy
  once a default workflow exists), browser uploads unavailable, and that it is not a production profile. The
  implementation must turn it into present tense.
- `production-operations.md`, Versioned Production Profile: new paragraph after the Migrator paragraph stating that the
  self-hosted evaluation composition (decided, not yet implemented) applies the fixed architecture choices and leaves
  every profile value Open / Blocking: loopback ingress without TLS, environment-file secrets, unsized single replicas,
  and engine-managed volume paths are evaluation choices, not approved values, and the composition passes no
  preflight. The implementation must drop "decided, not yet implemented".

### Status statements at merge

- `platform-implementation.md`, introduction: "the Web UI's offline mode, deployment configuration, and
  production-readiness evidence do not exist yet" → the evaluation composition exists; production deployment
  configuration and production-readiness evidence do not.
- `platform-implementation.md`, Source And Runtime Baseline: "Docker Compose is the initial deployment mechanism. It
  will compose the platform API and Web UI as separate OCI containers …" and "Future Compose deployments will also
  include PostgreSQL, NATS JetStream, and S3-compatible storage … plus the Migrator as a one-shot service …" → present
  tense for the evaluation composition, while production deployments stay future and Open / Blocking.
- `platform-implementation.md`, Source And Runtime Baseline: "Locally, the Aspire AppHost will mirror that order" →
  the AppHost mirrors that order (already implemented; correct only if still future tense at merge).
- `client-applications.md`, Source Ownership And Delivery: "… and Docker Compose deployment are not implemented yet" →
  the evaluation composition exists; production deployment stays unresolved.
- `production-operations.md`, introduction: "This topic does not provide deployment manifests." → it still provides none;
  add that the evaluation composition lives with the platform source and is not a production profile.
- `production-operations.md`, Versioned Production Profile: the new evaluation-composition paragraph's "decided, not
  yet implemented" → present tense.
- `production-operations.md`, Validation and Promotion Evidence: "a real Compose deployment" stays a production
  acceptance requirement that the evaluation composition does not satisfy.
- `overview.md`, Implementation Profile: the Provisional Docker Compose profile sentence gains that an evaluation
  composition exists, without approving the profile.

The documentation task in `tasks.md` must make these narratives and every status statement the feature affects true
for the merged state: it re-reads `docs/architecture/` at implementation time (including summaries, introductions, and
evidence lists, not only the sections above) and corrects every statement the merged composition makes false, using the
rewrites listed here as examples rather than the whole job, while keeping production profile values, preflight, backup
and restore, TLS, Analyst Manager connectivity, and Azure Container Apps explicitly unresolved or future.

## Project Structure

### Documentation (this feature)

```text
specs/20261018-094332-compose-stamp/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── compose-stamp.md
└── tasks.md
```

### Source Code (repository root)

```text
src/platform/
├── deploy/
│   └── compose/
│       ├── compose.yaml                  # new: one evaluation stamp
│       ├── stamp.env.template            # new: every required value, no secret defaults
│       └── README.md                     # new: setup, start, inspect, stop, teardown, second stamp, limitations
├── SocAlytics.Platform.StampSetup/       # new: setup console
│   ├── SocAlytics.Platform.StampSetup.csproj
│   ├── Program.cs                        # --output <dir>; refuses existing directories
│   ├── StampNatsTrust.cs                 # operator, stamp account, system account; nats.conf on /data
│   ├── StampSecrets.cs                   # cryptographic random secrets
│   └── StampEnvironmentFile.cs           # fills the template's secret values and SOCALYTICS_STAMP_DIR
├── SocAlytics.Platform.slnx              # changed: adds StampSetup and Compose.Tests
├── README.md                             # changed: evaluation composition and setup commands
└── Tests/
    ├── SocAlytics.Platform.Images.Tests/Support/ # changed: image build and workflow arrangement reusable by the Compose evidence
    └── SocAlytics.Platform.Compose.Tests/        # new: Docker Compose evidence
        ├── SocAlytics.Platform.Compose.Tests.csproj
        ├── compose.evidence.yaml         # evidence-only override: PostgreSQL on an ephemeral loopback port
        ├── Support/                      # compose CLI driver, stamp fixture, sign-in client
        ├── StampSetupTests.cs
        ├── ComposeDefinitionTests.cs     # required values, ingress, pinned images, no fixed names
        ├── StampLifecycleTests.cs        # order, readiness, sign-in, restart, Migrator failure, teardown
        └── StampIsolationTests.cs        # second project, no cross-stamp reachability, independent teardown

README.md                                 # changed: evaluation composition under Development
AGENTS.md                                 # changed: Current State and Repository Setup commands
docs/architecture/                        # changed: see Architecture Updates
```

**Structure Decision**: The composition lives with the platform it deploys, inside the covered platform scope; the
setup console is a platform project because it produces platform configuration; the evidence is one new platform test
project that reuses the image build fixture and the integration seeding helpers, as the image evidence does.

## Complexity Tracking

No violations.

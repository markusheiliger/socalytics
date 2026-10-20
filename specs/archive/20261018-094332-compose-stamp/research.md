# Research: Compose Stamp Composition for Self-Hosted Evaluation

All decisions build on the existing projects and on the image contract of the batch sibling
[Cloud-Neutral OCI Images](../20261018-092503-oci-images/contracts/container-images.md) (see [plan.md](plan.md)).
Values that belong to a production profile (host, engine and Compose versions, ports, DNS, certificates, secret source,
volume paths, replicas, resource sizes) stay **Open / Blocking** as
[Production Deployment and Operations](../../../docs/architecture/production-operations.md#versioned-production-profile)
defines; where the evaluation stamp needs a value, it is a documented evaluation choice or an operator-supplied value.

## R1 Location and form of the definition

- **Decision**: One Compose file `src/platform/deploy/compose/compose.yaml` (Compose Specification, run with
  `docker compose`), its environment file template `src/platform/deploy/compose/stamp.env.template`, and its
  documentation `src/platform/deploy/compose/README.md`. The definition sets no top-level `name`, no
  `container_name`, and no explicit network or volume `name`, so `docker compose -p <stamp>` scopes every resource to
  the project (FR-006). It builds no image: `image:` references come from required variables
  `SOCALYTICS_API_IMAGE`, `SOCALYTICS_MIGRATOR_IMAGE`, and `SOCALYTICS_WEB_IMAGE` (clarification).
- **Rationale**: The platform source area owns the platform's deployables (constitution Principle II); a nested
  `deploy/compose/` folder inside `src/platform/` is covered by the platform verification scope, so changes to it are
  verified. Required image variables keep the composition a deployment of built images, which is what both profiles do
  under the portability rules.
- **Alternatives considered**: A repository-root `deploy/` folder (a new top-level area outside every source area and
  uncovered by verification); `build:` sections (the composition would build from a source checkout and could diverge
  from the documented image builds); a fixed top-level `name` (would prevent a second stamp).

## R2 Variable interpolation and required values

- **Decision**: Every value the operator must supply is interpolated as `${NAME:?NAME is required}`, so
  `docker compose config` and `up` fail before any container starts and name the missing value (spec US1 scenario 4).
  Secrets have no default anywhere. The template lists every variable with a comment; the setup step (R5) writes the
  operator's stamp environment file from it. Operators pass the file with `--env-file <stamp dir>/stamp.env`.
- **Rationale**: Compose's required-variable syntax is the only mechanism that refuses to start before any container
  runs, which makes "no default for secrets" enforceable rather than documented.
- **Alternatives considered**: A `.env` file next to the Compose file (would place secrets inside the repository
  checkout); `env_file:` per service (does not fail on empty values).

## R3 Services, images, health checks, and order

- **Decision**:

  | Service | Image | Health check | Starts after |
  | --- | --- | --- | --- |
  | `postgres` | `postgres:18`, pinned by digest | `pg_isready -U postgres -d socalytics` | — |
  | `nats` | `nats:2.14-alpine`, pinned by digest | `wget -q -O /dev/null http://127.0.0.1:8222/healthz?js-enabled-only=true` | — |
  | `storage` | `rustfs/rustfs:1.0.1`, pinned by digest | `curl -fsS http://127.0.0.1:9000/health` | — |
  | `migrator` | `${SOCALYTICS_MIGRATOR_IMAGE}` | none (one-shot) | `postgres` healthy |
  | `api` | `${SOCALYTICS_API_IMAGE}` | none (image contract: no in-container probe; chiseled image has no tools) | `migrator` `service_completed_successfully`, `postgres`, `nats`, `storage` healthy |
  | `web` | `${SOCALYTICS_WEB_IMAGE}` | `wget -q -O /dev/null http://127.0.0.1:8080/` (BusyBox in the image) | `api` `service_started` |

  The Migrator has `restart: "no"`; long-running services use `restart: unless-stopped`. The API, Migrator, and Web UI
  run with `read_only: true` and a `tmpfs` at `/tmp`, as the image contract requires. No service sets `deploy.replicas`
  or resource limits (one replica; sizes stay Open / Blocking).
- **Rationale**: The same image versions as the AppHost (PostgreSQL 18, NATS 2.14, RustFS 1.0.1) keep the evaluation
  stamp on the dependency versions the tests exercise; the `-alpine` NATS variant carries BusyBox `wget` for the
  health check, and the RustFS image contains `curl` (both verified by inspecting the images). The API image cannot be
  probed from inside and the image contract forbids adding a probe, and the readiness rule may keep the API unhealthy
  until a default workflow exists (clarification), so nothing waits for API health; the Web UI already answers forwarded
  paths with `502` while the API is not reachable.
- **Alternatives considered**: A one-shot probe container that waits for `/health` (would block the stamp forever
  without a default workflow); `nats:2.14` scratch image (no tool for a health check); gating the Web UI on API health
  (impossible without an in-container probe).

## R4 Networks and ingress

- **Decision**: Four project-scoped networks: `data` (`internal: true`: `postgres`, `nats`, `storage`, `migrator`,
  `api`), `app` (`internal: true`: `api`, `web`), `edge` (`web` only; carries the published port), and `egress` (`api`
  only; reaches the configured SMTP relay and other configured external endpoints). Only `web` publishes a port:
  `127.0.0.1:${SOCALYTICS_WEB_PORT}:8080` (clarification: loopback only). Object storage is not published, the Web UI
  gets no `SOCALYTICS_OBJECT_STORAGE_ORIGIN`, and browser recording uploads are therefore unavailable (clarification).
- **Rationale**: Internal networks block both ingress and egress, so the data services and the Migrator have no route
  out of the stamp; the API's egress network makes its required external endpoints an explicit, documented boundary
  crossing as the profile rules require. Loopback binding avoids undocumented public exposure and plaintext transport
  over an untrusted boundary, both of which fail preflight.
- **Alternatives considered**: One flat default network (every service could reach the internet and the Web UI could
  reach the database); publishing object storage (the API signs upload URLs with its internal service address, so the
  browser could not use them without a separate public endpoint, which no option provides yet).

## R5 Setup step

- **Decision**: A new console project `src/platform/SocAlytics.Platform.StampSetup` (in the platform solution,
  referencing only `NATS.Jwt` and `NATS.NKeys`, which the AppHost already uses) run as
  `dotnet run --project src/platform/SocAlytics.Platform.StampSetup -- --output <stamp dir>`. It refuses an existing
    output directory and an output directory inside a Git working tree, creates it, and writes:
  - `nats.conf`: the NATS server configuration that trusts a new operator, stamp account, and system account
    (operator JWT, `system_account`, a full resolver with preloaded account JWTs), with the resolver and JetStream
    directories on the NATS data volume (`/data/resolver`, `/data/jetstream`) instead of the AppHost's `/tmp`;
  - `stamp.env`: the template with every secret filled (PostgreSQL administrator, migrator, and application role
    passwords; object storage access and secret keys; the three NATS seeds; the first Club Admin's initial credential)
    from a cryptographic random source (32 alphanumeric characters), `SOCALYTICS_STAMP_DIR` set to the absolute output
    path, and every non-secret value left empty for the operator. On Unix it restricts `stamp.env` to the owner.
  The seeds and JWTs follow the claims of `src/platform/SocAlytics.Platform.AppHost/LocalNatsTrust.cs` (operator,
  `SOCALYTICS` stamp account with unlimited JetStream limits, `SYS` system account). The project is documented with
  the alternative of running it in the pinned .NET SDK container for hosts without the SDK.
- **Rationale**: The platform's NATS JWT authentication needs Ed25519 NKeys and signed JWTs that shell tools cannot
  produce; the .NET libraries are already pinned in `Directory.Packages.props`, so no new package or tool is needed and
  the project is built and tested by the existing platform verification. Writing secrets only into a new directory the
  operator names keeps them out of the repository (FR-011).
- **Alternatives considered**: The `nsc` CLI or the `nats-box` image (a new tool and image, and a different file layout);
  committing trust material (forbidden); extending the AppHost (it is the development composition, not a deployment
  tool); reusing `LocalNatsTrust` by making it public (couples the AppHost to a deployment tool).

## R6 Database roles and connection strings

- **Decision**: `postgres` mounts the existing script `src/platform/SocAlytics.Platform.AppHost/PostgresInit/01-socalytics-roles.sh`
  read-only into `/docker-entrypoint-initdb.d/` and receives `POSTGRES_DB=socalytics`, `POSTGRES_PASSWORD`,
  `SOCALYTICS_MIGRATOR_PASSWORD`, and `SOCALYTICS_APP_PASSWORD`; its data volume is mounted at `/var/lib/postgresql`
  (the PostgreSQL 18 image's data root). The Migrator receives `ConnectionStrings__socalytics-migrator` with the
  `socalytics_migrator` role and the API `ConnectionStrings__socalytics` with the `socalytics_app` role, both pointing at
  `Host=postgres;Port=5432;Database=socalytics`.
- **Rationale**: Reusing the committed script keeps the roles identical to the AppHost's (FR-012) and the evidence
  proves the same separation the integration tests rely on.
- **Alternatives considered**: A copy of the script (two sources to keep equal); a superuser connection for the API
  (violates the migrator-only structure changes).

## R7 Object storage bucket

- **Decision**: The API runs with the existing `ObjectStorage__EnsureBucketOnStartup=true`, which creates the
  configured bucket idempotently at start; `ObjectStorage__ServiceUrl=http://storage:9000`, path-style, region
  `us-east-1`, bucket `socalytics-recordings`.
- **Rationale**: The option exists, is not restricted to an environment, and is idempotent; one API replica creates the
  bucket before it serves requests (FR-013) without another container.
- **Alternatives considered**: A one-shot `storage-init` service signing an S3 `PUT` with `curl --aws-sigv4` (more
  moving parts for the same result); manual creation (violates FR-013).

## R8 API configuration in the production environment

- **Decision**: The API runs without `ASPNETCORE_ENVIRONMENT` (Production, per the image contract) with every option it
  validates at start: the connection strings (R6); `ConnectionStrings__nats=nats://nats:4222`,
  `AnalystWork__QueueUrl=nats://nats:4222` (internal only; Analyst Managers are out of scope and cannot connect),
  `Nats__Authentication__Mode=Jwt` with the three seeds, `Messaging__Nats__ProvisionTopology=true`; object storage
  (R7); `Email__Smtp__Host`, `Email__Smtp__Port`, `Email__Smtp__Security` (`StartTls` or `SslOnConnect`, the only
  values allowed outside Development), optional relay credentials, sender address and name (operator values; the
  template documents the placeholder relay `smtp.invalid:587` with `StartTls`, which never delivers); `Stamp__Id`;
  `Stamp__PublicBaseUri` (an operator-supplied `https` placeholder; no endpoint behind it in the evaluation stamp);
  `Notifications__WebUiBaseUri` and `Registry__Pairing__VerificationBaseUri` set to `http://localhost:${SOCALYTICS_WEB_PORT}`;
  `ClubBootstrap__ClubDisplayName`, `ClubBootstrap__FirstClubAdmin__AccountName`, and the initial credential;
  `WebUi__MinimumVersion=${SOCALYTICS_RELEASE_VERSION}`; and the non-secret platform option values the AppHost passes
  (upload, email confirmation and delivery, notification, analysis result and read, identity session, lockout,
  password, and one-time-credential options) as documented evaluation choices. `Registry__SoftwareKeys`,
  `Nats__Authentication__Mode=None`, and every other Development- or Test-only relaxation stay unset (FR-017).
- **Rationale**: The production checks of the platform stay in force; the only difference from a production stamp is
  that the open profile values are evaluation choices or placeholders the documentation names.
- **Alternatives considered**: `ASPNETCORE_ENVIRONMENT=Development` (enables the development seed and relaxed checks
  and would not evaluate the real configuration); a separate evaluation environment name (new behavior in the platform).

## R9 Readiness and the default analysis workflow

- **Decision**: The composition does not change or bypass the readiness rule (clarification). The documentation states
  that `/health` reports healthy once the stamp's Registry holds a resolvable default analysis workflow. The evidence
  starts `postgres`, `migrator`, and the rest with an evidence-only override file
  `src/platform/Tests/SocAlytics.Platform.Compose.Tests/compose.evidence.yaml` that publishes PostgreSQL on an ephemeral
  loopback port, arranges the workflow with the existing configured-workflow seeding in-process in the Test environment
  (as `specs/archive/20261018-092503-oci-images` and `ReadinessProgressionTests` do), and then waits for `/health`.
- **Rationale**: The evidence must reach healthy (FR-020) without changing the platform; an override that only the
  evidence uses leaves the committed definition's ingress untouched, and the ingress assertions run against the
  committed definition alone (R10).
- **Alternatives considered**: Seeding through SQL from inside the `postgres` container (duplicates the seeding logic);
  importing a real Analyst image through the Registry (needs an image registry and an Analyst image, which a later
  feature provides).

## R10 Evidence design

- **Decision**: A new test project `src/platform/Tests/SocAlytics.Platform.Compose.Tests` (xUnit v3, Shouldly),
  referencing `SocAlytics.Platform.Images.Tests` for its image build fixture (the three images are built once with the
  documented commands) and `SocAlytics.Platform.Integration.Tests` for the configured-workflow seeding helpers, and
  `SocAlytics.Platform.StampSetup` to create disposable stamp directories under the system temporary directory (outside
  the working tree), which it deletes afterwards. It drives `docker compose` as a process with
  unique project names and removes every stamp with `down --volumes --remove-orphans` in its disposal. Checks:
  - `docker compose version` must succeed, otherwise the tests fail with a message naming the missing Compose support
    (FR-022);
  - `docker compose config` with an incomplete stamp file fails and names the missing value;
  - stamp A: Migrator exit code `0` before the API container's start time; `/alive` through `docker compose exec web
    wget` (the API has no tools); `/health` unhealthy with only the workflow check failing, then healthy within 1 minute
    after the workflow is arranged; sign-in through `http://127.0.0.1:<port>` with the bootstrap Club Admin and the
    existing sign-in, required-password-change, and profile operations (cookies handled by the test, since the session
    cookie is `Secure` and the evaluation origin is plain HTTP on loopback);
  - ingress: the committed definition's `docker compose config --format json` publishes exactly one port, on `web`,
    bound to `127.0.0.1`;
  - restart: `docker compose stop` and `start` keep the club and the Migrator applies nothing;
  - stamp B (committed definition without the override, other port and secrets): its own club bootstraps; from B's
    `web` container, connections to A's `api`, `postgres`, `nats`, and `storage` container addresses fail (BusyBox
    `wget`/`nc` with short timeouts), and A's names do not resolve;
  - teardown: no container, network, or volume labelled with either project remains; A's teardown leaves B running.
  The Migrator failure scenario uses a stamp file with a wrong migrator password and asserts that the API container is
  never created.
- **Rationale**: The same CLI the operator uses produces the evidence, and the image build fixture is shared with the
  image evidence, so the composition is tested against the images the sibling feature proves.
- **Alternatives considered**: Testcontainers' Compose module (not in the pinned package set and hides the CLI the
  operator uses); a Playwright browser test (the Web UI's sign-in in a browser is already covered by its own tests; the
  stamp-level proof needs only the same-origin HTTP flow).

## R11 Environment check

The plan needs the .NET 10 SDK and Docker with the Compose v2 plugin (`docker compose`). The SDK is installed by
`environment-setup`; Docker is on the runner, as `environment-setup` states and the Docker-backed platform tests and the
image evidence of `specs/archive/20261018-092503-oci-images` rely on, and the GitHub-hosted `ubuntu-latest` runner image ships
the Compose v2 plugin with its Docker installation. No new framework, SDK, or package is needed (`NATS.Jwt` and
`NATS.NKeys` are already pinned). No environment spec is needed; the evidence fails visibly with a named message when
`docker compose` is unavailable. The new files live in the covered platform scope, and the evidence runs with the
platform tests.

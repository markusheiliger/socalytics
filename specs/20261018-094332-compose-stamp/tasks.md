---

description: "Task list for Compose Stamp Composition for Self-Hosted Evaluation"
---

# Tasks: Compose Stamp Composition for Self-Hosted Evaluation

**Input**: Design documents from `specs/20261018-094332-compose-stamp/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated Compose evidence (FR-020 to FR-022, SC-001 to SC-006), so every story with
behavior has test tasks. Test tasks follow the files they exercise so that each landed task keeps the build and test
suites green.

**Organization**: Tasks are grouped by user story. The batch sibling `specs/20261018-092503-oci-images` must be merged
first: the evidence reuses its image build fixture and workflow arrangement under
`src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/`.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The environment file template, reusable image and workflow helpers, the setup console, and the empty
evidence project with its Compose driver

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Template and reusable helpers

- [ ] T001 [P] Add the environment file template `src/platform/deploy/compose/stamp.env.template` with every variable of contracts/compose-stamp.md §4 in that order, one comment line per variable naming its kind (setup, operator, secret, optional secret), its meaning, and whether it is a production profile value that stays Open / Blocking; every value empty (no default for any value, in particular no secret); the SMTP comments name the never-delivering placeholder relay `smtp.invalid`, port `587`, security `StartTls`, and that `StartTls` or `SslOnConnect` are the only values allowed outside Development; a header comment states that the file is for self-hosted evaluation, is not a production profile, and that the setup step fills the secrets (research R2; FR-015, FR-016; spec US4 scenario 1)
- [ ] T002 [P] Make the image evidence helpers reusable by another test project, without changing the image tests' behavior: in `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/ImageBuildFixture.cs` expose a public way to build the API, Migrator, and Web UI images once per run and return their tags and the fixed test release version; in `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/StampDependenciesFixture.cs` extract the default-workflow arrangement (the existing configured-workflow seeding run in-process in the Test environment) into a public helper `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/DefaultWorkflowArrangement.cs` that takes an application-role connection string, and call it from the fixture (research R9, R10; FR-020)

### Setup console

- [ ] T003 Add the setup console `src/platform/SocAlytics.Platform.StampSetup/` and register it in `src/platform/SocAlytics.Platform.slnx`: `SocAlytics.Platform.StampSetup.csproj` (console, package references `NATS.Jwt` and `NATS.NKeys` only, no platform project reference, `src/platform/deploy/compose/stamp.env.template` embedded as a linked resource); `Program.cs` (`--output <dir>`; exits non-zero and writes nothing when the directory exists or lies inside a Git working tree, that is when it or an ancestor contains `.git`; prints the next steps); `StampNatsTrust.cs` (a new operator, `SOCALYTICS` stamp account with unlimited limits and JetStream, and `SYS` system account with the claims of `src/platform/SocAlytics.Platform.AppHost/LocalNatsTrust.cs`, rendering `nats.conf` with `port: 4222`, `http_port: 8222`, the operator JWT, `system_account`, a full resolver in `/data/resolver` with both account JWTs preloaded, and `jetstream.store_dir` `/data/jetstream`); `StampSecrets.cs` (32 alphanumeric characters from a cryptographic random source per secret); `StampEnvironmentFile.cs` (writes `stamp.env` from the embedded template with every secret of contracts/compose-stamp.md §4 and the three NATS seeds filled, `SOCALYTICS_STAMP_DIR` set to the absolute output path, every operator value left empty, and on Unix file mode `0600`) (research R5; data-model "Setup output"; FR-010, FR-011)

### Evidence project

- [ ] T004 Create the evidence project `src/platform/Tests/SocAlytics.Platform.Compose.Tests/SocAlytics.Platform.Compose.Tests.csproj` (xUnit v3, Shouldly, Npgsql, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` as in `src/platform/Tests/SocAlytics.Platform.Images.Tests/SocAlytics.Platform.Images.Tests.csproj`; project references to `SocAlytics.Platform.Images.Tests` and `SocAlytics.Platform.StampSetup`) and add it to `src/platform/SocAlytics.Platform.slnx`; add `src/platform/Tests/SocAlytics.Platform.Compose.Tests/Support/ComposeCli.cs` (runs `docker compose -f src/platform/deploy/compose/compose.yaml [-f <override>] -p <project> --env-file <file> <args>` as a process from the repository root, returns exit code and output, and fails the test with a message naming the missing Docker or Compose support when `docker compose version` fails) and `src/platform/Tests/SocAlytics.Platform.Compose.Tests/Support/StampFixture.cs` (creates a stamp directory under the system temporary directory with the setup console, fills the operator values with disposable values (the image tags and release version from `ImageBuildFixture`, a free loopback port, a unique stamp ID and club name, an `https://stamp.invalid` public base URI, the placeholder relay `smtp.invalid`/`587`/`StartTls`, a sender), uses a unique project name, and on dispose runs `down --volumes --remove-orphans` and deletes the directory); no test class yet (research R10; FR-022)

**Checkpoint**: Foundation ready; the solution builds and every existing test stays green

---

## Phase 2: User Story 1 - Bring up one evaluation stamp on one host (Priority: P1) 🎯 MVP

**Goal**: The committed composition starts one stamp in dependency order with only the Web UI published on loopback,
the API in its production environment, and unchanged readiness

**Independent Test**: Start the composition with disposable values under one project name; the Migrator completes
before the API starts, the API is alive, only the Web UI is published, and the stamp is healthy once a default
workflow exists

### Composition and setup tests

- [ ] T005 [P] [US1] Add the stamp definition `src/platform/deploy/compose/compose.yaml` exactly as contracts/compose-stamp.md §3 and §5 define: services `postgres` (`postgres:18` pinned by digest, `POSTGRES_DB=socalytics`, the three passwords, the roles script `../../SocAlytics.Platform.AppHost/PostgresInit/01-socalytics-roles.sh` mounted read-only into `/docker-entrypoint-initdb.d/`, volume `postgres-data` at `/var/lib/postgresql`, health check `pg_isready -U postgres -d socalytics`), `nats` (`nats:2.14-alpine` pinned by digest, `${SOCALYTICS_STAMP_DIR}/nats.conf` mounted read-only and passed with `-c`, volume `nats-data` at `/data`, BusyBox `wget` health check on `http://127.0.0.1:8222/healthz?js-enabled-only=true`), `storage` (`rustfs/rustfs:1.0.1` pinned by digest, the storage keys, volume `storage-data` at `/data`, `curl` health check on `http://127.0.0.1:9000/health`), `migrator` (`${SOCALYTICS_MIGRATOR_IMAGE}`, `ConnectionStrings__socalytics-migrator` with role `socalytics_migrator` on `Host=postgres;Port=5432;Database=socalytics`, `restart: "no"`, depends on `postgres` healthy), `api` (`${SOCALYTICS_API_IMAGE}`, no `ASPNETCORE_ENVIRONMENT`, every option of contracts/compose-stamp.md §5 including the non-secret option values the API receives in `src/platform/SocAlytics.Platform.AppHost/Program.cs` with production-valid values, depends on `migrator` `service_completed_successfully` and `postgres`, `nats`, `storage` `service_healthy`, networks `data`, `app`, `egress`), and `web` (`${SOCALYTICS_WEB_IMAGE}`, `SOCALYTICS_API_UPSTREAM=http://api:8080`, no object storage origin, port `127.0.0.1:${SOCALYTICS_WEB_PORT}:8080`, BusyBox `wget` health check on `http://127.0.0.1:8080/`, depends on `api` `service_started`, networks `app`, `edge`); `migrator`, `api`, and `web` with `read_only: true` and `tmpfs: /tmp`; long-running services `restart: unless-stopped`; networks `data` and `app` with `internal: true`, `edge`, and `egress`; the three named volumes; every operator and secret value interpolated as `${NAME:?NAME is required}` except the optional relay credentials; no top-level `name`, no `container_name`, no fixed network or volume `name`, no `build`, no `deploy` (research R1 to R4, R6 to R8; FR-001 to FR-010, FR-012 to FR-014, FR-017)
- [ ] T006 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Compose.Tests/StampSetupTests.cs`: the console creates `stamp.env` and `nats.conf` in a new directory under the system temporary directory; every secret variable of contracts/compose-stamp.md §4 has a non-empty 32-character value, the three seeds parse as NATS operator and account seeds whose public keys appear in `nats.conf`, `SOCALYTICS_STAMP_DIR` is the absolute output path, and every operator value is empty; a second run against the same directory exits non-zero and changes no file; a run inside the repository checkout exits non-zero and creates nothing; on Unix `stamp.env` has mode `0600` (FR-011; SC-006; spec edge cases)

### Definition and lifecycle evidence

- [ ] T007 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Compose.Tests/ComposeDefinitionTests.cs` (no change to `Support/`): with a complete disposable stamp file, `docker compose config --format json` of the committed definition alone publishes exactly one port, on `web`, bound to `127.0.0.1`; every `postgres`, `nats`, and `storage` image reference contains `@sha256:`; no service has `container_name`, `build`, or `deploy`, and no network or volume has a fixed `name`; `data` and `app` are internal; `migrator`, `api`, and `web` are read-only with a `/tmp` tmpfs; `migrator` does not restart and `api` depends on it with `service_completed_successfully`; `api` has no `ASPNETCORE_ENVIRONMENT`, `Nats__Authentication__Mode` is `Jwt`, and no Development- or Test-only option (`Registry__SoftwareKeys`, `Nats__Authentication__Mode=None`, `Registry__Images__UseHttp`) is set; `web` has no `SOCALYTICS_OBJECT_STORAGE_ORIGIN`; every variable the definition interpolates is listed in `src/platform/deploy/compose/stamp.env.template` and vice versa, and the template assigns no value; removing one required value from the stamp file makes `config` fail with a message that names it (FR-002 to FR-009, FR-015 to FR-017; SC-003, SC-006; spec US1 scenario 4, US4 scenario 1)
- [ ] T008 [P] [US1] Add the evidence-only override `src/platform/Tests/SocAlytics.Platform.Compose.Tests/compose.evidence.yaml` (publishes `postgres` port 5432 on an ephemeral `127.0.0.1` port and nothing else; `ComposeCli` passes it by its repository path, so no project-file change) and `src/platform/Tests/SocAlytics.Platform.Compose.Tests/StampLifecycleTests.cs` using `StampFixture`: `up -d` with the override; within 5 minutes `postgres`, `nats`, `storage`, and `web` are healthy and the Web UI origin serves the shell; the `migrator` container exited `0` and its finish time precedes the `api` container's start time; `/alive` through `docker compose exec web wget -qO- http://api:8080/alive` answers within 3 minutes of `up`; `/health` is unhealthy with only the default-workflow check failing; after `DefaultWorkflowArrangement` runs against the published PostgreSQL port with the application role, `/health` is healthy within 1 minute without a restart; the bucket `socalytics-recordings` exists in `storage`; restarting the `nats` container brings `/health` back to healthy within 1 minute and keeps the JetStream streams on its volume; `stop` then `start` keeps the club and the migration journal row count unchanged and the Migrator exits `0` again; a second stamp whose migrator password is wrong ends with a non-zero Migrator exit code and no `api` container; after the fixture's teardown no container, network, or volume labelled with either project name remains (research R9, R10; FR-004, FR-005, FR-013, FR-020; SC-001, SC-002, SC-004; spec US1 scenarios 1, 2, 5, 6)

**Checkpoint**: One evaluation stamp starts, orders, reaches healthy with a default workflow, and tears down cleanly

---

## Phase 3: User Story 2 - Sign in to the evaluation stamp through one origin (Priority: P1)

**Goal**: The bootstrap Club Admin signs in through the Web UI's published origin and completes the required password
change

**Independent Test**: With a started stamp, sign in through `http://127.0.0.1:<port>` with the bootstrap account and
its initial credential, change the password, sign in again, and read the member's own profile

### Sign-in evidence

- [ ] T009 [US2] Add `src/platform/Tests/SocAlytics.Platform.Compose.Tests/Support/OriginClient.cs` (an HTTP client for the published Web UI origin that keeps the session cookie and anti-forgery token itself, because the session cookie is `Secure` and the evaluation origin is plain HTTP on loopback, and calls the existing sign-in, required-password-change, and own-profile operations under `/api/v1` as the Web UI's adapters in `src/clients/web/src/` call them and as `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/SessionEndpointTests.cs` exercises them) and `src/platform/Tests/SocAlytics.Platform.Compose.Tests/StampSignInTests.cs` using `StampFixture` without the override: the origin serves the HTML shell and an API request through it reaches the API; signing in with `SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME` and the initial credential from the stamp file establishes a session that requires a password change; after the change, a new sign-in with the new password reaches the own profile with the Club Admin role (research R10; FR-020; spec US2 scenarios 1 to 3)

**Checkpoint**: The evaluation stamp is usable through its one origin

---

## Phase 4: User Story 3 - Run several isolated stamps side by side (Priority: P2)

**Goal**: Two project names yield two isolated stamps that can be removed independently

**Independent Test**: Start two stamps with different project names and values; each bootstraps its own club, neither
reaches the other's services, and removing one leaves the other running

### Isolation evidence

- [ ] T010 [US3] Add `src/platform/Tests/SocAlytics.Platform.Compose.Tests/StampIsolationTests.cs` using two `StampFixture` instances without the override: both stamps start with different ports and secrets and each bootstraps its own club (the club display name through each origin); every container, network, and volume carries exactly one of the two project labels and no name is shared; from stamp B's `web` container, connections to stamp A's `api` (8080), `postgres` (5432), `nats` (4222), and `storage` (9000) container addresses, read with `docker inspect`, fail within a short timeout (BusyBox `wget -T` or `nc -w`), and A's service names resolve only to B's own services; tearing down stamp A with its volumes leaves stamp B's containers running and its club unchanged; starting a third stamp with stamp B's published port fails with a port conflict while stamp B keeps running; after all teardowns no resource of any project remains (FR-006, FR-021; SC-004, SC-005; spec US3 scenarios 1 to 3; spec edge cases)

**Checkpoint**: Stamps on one host are isolated and independently removable

---

## Phase 5: User Story 4 - Know exactly what the evaluation stamp is and is not (Priority: P2)

**Goal**: The documentation names every required value, every open profile value, the limitations, and that the
composition is not a production profile

**Independent Test**: Read the documentation and template: every required value is named, no secret has a default,
every open profile value is a placeholder, and the non-production statement is present

### Composition documentation

- [ ] T011 [US4] Add `src/platform/deploy/compose/README.md`: prerequisites (Docker Engine with the Compose v2 plugin, the .NET SDK or the pinned SDK container for the setup console, the images built with the `docker build` commands that `specs/20261018-092503-oci-images` documents in `README.md`); the setup step with an output directory outside the repository; filling the operator values of `stamp.env`; the start, `ps`, `logs`, `stop`, `start`, and `down --volumes --remove-orphans` commands of contracts/compose-stamp.md §2; signing in at `http://localhost:<port>` with the bootstrap account and the required password change; running a second stamp under another project name, port, and stamp directory; the service, network, and volume overview; and a limitations section stating that the composition is for self-hosted evaluation, is not a production profile, passes no production preflight, does not replace the Aspire AppHost, publishes the Web UI only on loopback over plain HTTP without TLS, offers no browser recording uploads, lets no Analyst Manager connect, delivers no mail with the placeholder relay, reports `/health` healthy only once the Registry holds a resolvable default analysis workflow, and leaves host, ports, DNS, certificates, secret source, volume paths, replicas, and resource sizes Open / Blocking as `docs/architecture/production-operations.md` defines (FR-016, FR-018, FR-019; spec US4 scenario 2)

**Checkpoint**: Operators know how to run the stamp and what it does not provide

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Repository documentation, the architecture for the merged state, and the final validation

### Repository and architecture documentation

- [ ] T012 [P] Document the evaluation composition with the repository's other commands: add an "Evaluation stamp (Docker Compose)" subsection under "Development" in `README.md` with the setup command, the `up` and `down --volumes --remove-orphans` commands, the evidence command `dotnet test src/platform/Tests/SocAlytics.Platform.Compose.Tests`, a link to `src/platform/deploy/compose/README.md`, and the statement that it is not deployment support or production readiness; add the composition, the setup console, and the evidence project to `src/platform/README.md`; update `AGENTS.md` ("Current State": the evaluation composition, the setup console, and the Compose evidence project, keeping deployment configuration and production readiness unresolved; "Repository Setup": the setup and Compose commands and that the Compose tests need Docker with the Compose v2 plugin) (FR-018, FR-019)
- [ ] T013 [P] Make `docs/architecture/` describe the merged state (plan.md "Architecture Updates"): re-read `docs/architecture/` at implementation time, including summaries, introductions, and evidence lists, and correct every statement the merged composition makes false, for example in `docs/architecture/platform-implementation.md` (introduction, Source And Runtime Baseline), `docs/architecture/client-applications.md` (Source Ownership And Delivery), `docs/architecture/production-operations.md` (introduction, Versioned Production Profile, Validation and Promotion Evidence), `docs/architecture/tenancy-and-technology.md` (Initial Production Profile), and `docs/architecture/overview.md` (Implementation Profile); turn the design paragraphs worded as decided but not implemented into present tense, matching what was built; keep production profile values, preflight, backup and restore, TLS, Analyst Manager connectivity, browser uploads in the evaluation stamp, and Azure Container Apps explicitly unresolved or future, and never present the composition as a production profile or deployment readiness (constitution Principle I)

### Final validation

- [ ] T014 Run the validation of `specs/20261018-094332-compose-stamp/quickstart.md`: build the three images with the commands of `specs/20261018-092503-oci-images/quickstart.md`, run the setup console and the `docker compose` commands for one stamp and a second stamp and tear both down, run `dotnet build src/platform/SocAlytics.Platform.slnx` and `dotnet test src/platform/Tests/SocAlytics.Platform.Compose.Tests`, and run `node .github/scripts/check-markdown.mjs`; fix any failure in the files of the task that introduced it

---

## Dependencies & Execution Order

### Phase Dependencies

- **Prerequisite**: `specs/20261018-092503-oci-images` is merged (images, `ImageBuildFixture`, `StampDependenciesFixture`)
- **Foundational (Phase 1)**: No dependency inside this feature; blocks all stories
- **US1 Stamp (Phase 2)**: Depends on Phase 1
- **US2 Sign-in (Phase 3)**: Depends on Phase 2 (needs the definition)
- **US3 Isolation (Phase 4)**: Depends on Phase 2
- **US4 Documentation (Phase 5)**: Depends on Phase 2 (documents the definition)
- **Polish (Phase 6)**: Depends on all stories

### Within Each Phase

- T001 and T002 change disjoint files; T003 embeds T001's template and changes `SocAlytics.Platform.slnx`, so it runs
  after them; T004 references T002's helpers and T003's project and also changes `SocAlytics.Platform.slnx`, so it runs
  alone after T003
- T005 (definition) and T006 (setup tests) change disjoint files; T007 and T008 both run the definition of T005 and
  change disjoint files (neither changes `Support/`)
- T009 and T010 each run alone in their phase

### Parallel Opportunities

- Phase 1: T001 (template) with T002 (image test helpers)
- Phase 2: T005 (definition) with T006 (setup tests); then T007 (definition tests) with T008 (lifecycle tests)
- Phase 6: T012 (READMEs and `AGENTS.md`) with T013 (`docs/architecture/`)

---

## Parallel Example: User Story 1

```text
Task: "T005 Add src/platform/deploy/compose/compose.yaml"
Task: "T006 Add src/platform/Tests/SocAlytics.Platform.Compose.Tests/StampSetupTests.cs"
# then
Task: "T007 Add src/platform/Tests/SocAlytics.Platform.Compose.Tests/ComposeDefinitionTests.cs"
Task: "T008 Add compose.evidence.yaml and StampLifecycleTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1)

1. Complete Phase 1: Foundational
2. Complete Phase 2: the definition and its lifecycle evidence
3. **STOP and VALIDATE**: one stamp starts in order, reaches healthy with a default workflow, and tears down cleanly

### Incremental Delivery

1. Foundation → one stamp (MVP)
2. Sign-in through the one origin
3. Isolation of a second stamp
4. Operator documentation, repository documentation, and the architecture for the merged state

---

## Notes

- [P] tasks change different files and have no dependencies on each other
- The Compose tests build the three images once per run and need Docker with the Compose v2 plugin
- Never change `.github/`, the AppHost composition, the images, or any production profile value; never commit a stamp
  directory, secret, or NATS trust material

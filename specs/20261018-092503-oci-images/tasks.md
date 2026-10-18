---

description: "Task list for Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI"
---

# Tasks: Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI

**Input**: Design documents from `specs/20261018-092503-oci-images/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated container evidence (FR-026, FR-027, SC-001 to SC-007), so every story has test
tasks. Test tasks follow the image definitions of their story so that each landed task keeps the build and test suites
green.

**Organization**: Tasks are grouped by user story. The Migrator story (US2) comes before the API story (US1), although
both are P1, because the API evidence prepares its database with the Migrator image (spec clarifications).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint
  files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3, US4)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: The single Web UI policy source and build split, the publish exclusion, and the empty evidence project with
its image build and container helpers

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

- [X] T001 [P] Make the Web UI's production security headers one source and split its build: add `src/clients/web/build/securityHeaders.ts` exporting the production Content Security Policy directives (exactly the list now in `src/clients/web/vite.config.ts`, with `connect-src 'self'` plus an optional object storage origin), a function that renders the policy for a given optional origin, and the common headers (`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Permissions-Policy: camera=(), microphone=(), geolocation=()`); make `src/clients/web/vite.config.ts` import them for `preview` (the development policy stays in `vite.config.ts`); in `src/clients/web/package.json` add the script `"build:bundle": "tsc -b && vite build"` and change `build` to `npm run generate && npm run build:bundle` (research R3, R5; FR-022)
- [X] T002 [P] Stop publishing development settings: in `src/platform/SocAlytics.Platform.Api/SocAlytics.Platform.Api.csproj` mark `appsettings.Development.json` with `CopyToPublishDirectory="Never"` (keeping it for local runs and tests) (research R2; FR-011)
- [X] T003 [P] Create the evidence project `src/platform/Tests/SocAlytics.Platform.Images.Tests/SocAlytics.Platform.Images.Tests.csproj` (xUnit v3, Shouldly, Testcontainers, Npgsql, `Microsoft.NET.Test.Sdk`, `xunit.runner.visualstudio` as in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/SocAlytics.Platform.EndToEnd.Tests.csproj`; a project reference to `SocAlytics.Platform.Integration.Tests`; the linked `src/platform/SocAlytics.Platform.AppHost/PostgresInit/01-socalytics-roles.sh` copied to the output); add it to `src/platform/SocAlytics.Platform.slnx`; add `<InternalsVisibleTo Include="SocAlytics.Platform.Images.Tests" />` to `src/platform/SocAlytics.Platform.Infrastructure/SocAlytics.Platform.Infrastructure.csproj` and, in the same task, extend the allow-list in `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/PersistenceArchitectureTests.cs` (rename `Infrastructure_exposes_internals_only_to_the_migrator_and_the_integration_and_end_to_end_tests` to name the image evidence tests too, and add `"SocAlytics.Platform.Images.Tests"` to the expected `InternalsVisibleTo` set; the test keeps asserting an exact set, so no other assembly gains access); add `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/ImageBuildFixture.cs` (an xUnit collection fixture that locates the repository root, builds a requested image once per run with the documented command `docker build -f <Dockerfile> --build-arg SOCALYTICS_REVISION=<fixed 40-hex test revision> --build-arg SOCALYTICS_VERSION=<fixed YYYY.MMDD.HHMMSS test version> -t <unique tag> .` through the Docker CLI, captures the build output for failures, removes its images on dispose, and fails visibly when no container runtime or `docker` CLI is available) and `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/ContainerProbe.cs` (helpers that start a container with a read-only root filesystem and a tmpfs at `/tmp` on a given Testcontainers network, read the image configuration (user, labels, healthcheck, environment, history) and the container inspection (`ReadonlyRootfs`, exit code), read the process UIDs through the Docker Engine `top` operation, and list an image's files from an exported container filesystem); no test class yet (research R1, R8; FR-026, FR-027)

**Checkpoint**: Foundation ready; the solution builds and every existing test stays green

---

## Phase 2: User Story 2 - Apply migrations with the Migrator image (Priority: P1)

**Goal**: The Migrator image runs the existing Migrator once and exits with its outcome code, as a non-root user on a
read-only root filesystem

**Independent Test**: Run the Migrator image against an empty disposable database (exit `0`, all migrations in the
journal), again (exit `0`, nothing applied), and without configuration (exit `2`)

### Implementation for User Story 2

- [X] T004 [US2] Add the Migrator image definition `src/platform/SocAlytics.Platform.Migrator/Dockerfile` and its allow-list `src/platform/SocAlytics.Platform.Migrator/Dockerfile.dockerignore` (`*` excluded; re-include `src/platform/global.json`, `src/platform/Directory.Build.props`, `src/platform/Directory.Packages.props`, the Domain, Application, Infrastructure, ServiceDefaults, and Migrator project folders, and `contracts/**/*.schema.json`; keep `**/bin`, `**/obj`, `**/appsettings.Development.json`, `**/.env*`, and test folders excluded): build stage `mcr.microsoft.com/dotnet/sdk` with an exact 10.0.4xx tag pinned by digest that fails with a message naming the argument when `SOCALYTICS_REVISION` is empty or `SOCALYTICS_VERSION` does not match `^[0-9]{4}\.[0-9]{4}\.[0-9]{6}$`, restores and publishes only `SocAlytics.Platform.Migrator` (`-c Release`, framework-dependent, `UseAppHost=false`) to `/app`; final stage `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra` pinned by digest with only `/app`, `ENV TMPDIR=/tmp`, `USER 1654`, `ENTRYPOINT ["dotnet", "/app/SocAlytics.Platform.Migrator.dll"]`, no `HEALTHCHECK`, and the labels of contracts/container-images.md §2 with title `socalytics-migrator` (FR-001 to FR-005, FR-007 to FR-013, FR-017, FR-018, FR-028)

### Tests for User Story 2

- [X] T005 [US2] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/MigratorImageTests.cs` using `ImageBuildFixture` and `ContainerProbe`: on a dedicated network, start PostgreSQL with the roles script (as `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Infrastructure/PostgresContainerFixture.cs` does); run the Migrator image read-only with `/tmp` tmpfs and `ConnectionStrings__socalytics-migrator` and assert exit `0` and one journal entry per embedded migration script; run it again and assert exit `0` with an unchanged journal; run it without a connection string and assert exit `2`; run it against an unreachable host with a short `Migrator__ConnectTimeout` and assert exit `3`; assert the process UID is 1654, `ReadonlyRootfs` is true, the container has exited, and the image's file listing contains no `SocAlytics.Platform.Api.dll`; run it once more with an arbitrary non-root user (for example `12345:0`) and no connection string and assert it still exits `2` (spec US2 scenarios 1 to 4; SC-003, SC-005)

**Checkpoint**: The Migrator image is usable and proven on its own

---

## Phase 3: User Story 1 - Run the platform API from its image (Priority: P1) 🎯 MVP

**Goal**: The API image runs the existing host in its production environment, reports `/alive` and `/health`, contains
no migration path, and runs non-root on a read-only root filesystem

**Independent Test**: Prepare the database with the Migrator image and a resolvable default workflow, start the API
image with environment variables only, and probe `/alive` and `/health`

### Implementation for User Story 1

- [x] T006 [P] [US1] Add the API image definition `src/platform/SocAlytics.Platform.Api/Dockerfile` and its allow-list `src/platform/SocAlytics.Platform.Api/Dockerfile.dockerignore` (as T004 but re-including the Api project folder instead of the Migrator folder): the same validated build stage publishing only `SocAlytics.Platform.Api`; final stage `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` pinned by digest with only `/app`, `ENV TMPDIR=/tmp ASPNETCORE_HTTP_PORTS=8080`, `EXPOSE 8080`, `USER 1654`, `ENTRYPOINT ["dotnet", "/app/SocAlytics.Platform.Api.dll"]`, no `HEALTHCHECK`, no `ASPNETCORE_ENVIRONMENT` (so the host runs as Production), and the labels of contracts/container-images.md §2 with title `socalytics-api` (FR-001 to FR-016, FR-028)
- [X] T007 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/StampDependenciesFixture.cs`: one network with aliases for PostgreSQL (roles script), NATS with the JWT trust configuration that `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Infrastructure/NatsJwtContainerFixture.cs` uses, and RustFS (as `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Recordings/RustFsContainerFixture.cs`); it runs the Migrator image (via `ImageBuildFixture`) to prepare the schema, then arranges a resolvable default analysis workflow by running the existing configured-workflow seeding in-process in the Test environment against the same database (as `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Analysis/ReadinessProgressionTests.cs` does), and exposes the complete production option set for the API container as environment variables (connection strings with the application role, NATS JWT seeds, object storage, `Messaging__Nats__ProvisionTopology=true`, an SMTP host that is never contacted with `StartTls`, `Stamp__Id`, an `https` `Stamp__PublicBaseUri`, club bootstrap, identity session, lockout, password, and one-time-credential options, `WebUi__MinimumVersion`, and every other option the API validates at start, taken from the API environment in `src/platform/SocAlytics.Platform.AppHost/Program.cs` with values valid in the production environment) (spec clarifications; research R8)

### Tests for User Story 1

- [X] T008 [US1] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/ApiImageTests.cs` using `StampDependenciesFixture`: start the API image read-only with `/tmp` tmpfs and only environment variables; assert `/alive` answers within 30 s and `/health` reports healthy within 60 s; assert the process UID is 1654 and `ReadonlyRootfs` is true; start one with an arbitrary non-root user (for example `12345:0`) and assert `/alive` answers; start a second container with an unreachable database and assert `/alive` is alive while `/health` is unhealthy; start one without a required option (for example `Stamp__Id`) and assert it exits non-zero with the options-validation failure in its log; assert the image's file listing contains no `SocAlytics.Platform.Migrator.dll` and no `dbup*.dll` and that `appsettings.Development.json` is absent (spec US1 scenarios 1 to 5; FR-014 to FR-016; SC-002, SC-005)

**Checkpoint**: MVP: the Migrator and API images run a stamp's control plane from images

---

## Phase 4: User Story 3 - Serve the Web UI from its image on one origin with the API (Priority: P2)

**Goal**: The Web UI image serves the built PWA byte for byte with the production headers and forwards `/api` and
`/alive` to the API container

**Independent Test**: Start the Web UI image in front of the API image and check the shell, integrity, headers, caching,
fallback, and forwarding through the one origin

### Server configuration

- [X] T009 [US3] Add the server configuration `src/clients/web/container/nginx.conf` (no default server; `pid`, `client_body_temp_path`, `proxy_temp_path`, `fastcgi_temp_path`, `uwsgi_temp_path`, and `scgi_temp_path` under `/tmp`; includes the rendered server from `/tmp/nginx/conf.d`; `types` extended with `application/manifest+json webmanifest`) and the template `src/clients/web/container/default.conf.template` implementing contracts/container-images.md §6: listen 8080; static rules of §6.2 (no-cache for `index.html`, `sw.js`, `shell-manifest.json`, `manifest.webmanifest`, and icons; `public, max-age=31536000, immutable` for `/assets/`; 404 for missing paths with a file extension or under `/assets/`; `index.html` for other paths); the security headers of §6.3 with the Content Security Policy text of `src/clients/web/build/securityHeaders.ts` and `${SOCALYTICS_OBJECT_STORAGE_ORIGIN}` appended to `connect-src`, set with `always` on every response the server produces; forwarding of `/api`, `/api/`, and `/alive` per §6.4 to `${SOCALYTICS_API_UPSTREAM}` resolved per request through `${NGINX_LOCAL_RESOLVERS}`, with `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host`, `proxy_http_version 1.1`, `proxy_request_buffering off`, `proxy_buffering off`, `client_max_body_size 0`, one-hour `proxy_read_timeout` and `proxy_send_timeout`, and the API's response headers passed unchanged (research R4, R5; FR-019 to FR-025)

### Image definition and policy test

- [x] T010 [P] [US3] Add the Web UI image definition `src/clients/web/Dockerfile` and its allow-list `src/clients/web/Dockerfile.dockerignore` (`*` excluded; re-include the `src/clients/web` sources, configuration, `package.json`, `package-lock.json`, `public/`, `build/`, `container/`, and `src/platform/SocAlytics.Platform.Api/openapi/v1.json`; keep `node_modules`, `dist`, `src/generated`, `test-results`, `playwright-report`, `tests`, and `.env*` excluded): stage 1 `mcr.microsoft.com/dotnet/sdk` (the digest of T004) validates `SOCALYTICS_REVISION` and `SOCALYTICS_VERSION` as T004 does, installs Kiota 1.36.0 as a tool, and generates the client with the arguments of the `generate` script; stage 2 `node:24-bookworm-slim` pinned by digest runs `npm ci` with `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`, copies the generated client, and runs `npm run build:bundle` with `SOCALYTICS_WEB_VERSION` set to `SOCALYTICS_VERSION`; final stage `nginxinc/nginx-unprivileged` (Alpine) pinned by digest with only `dist/` in the web root, `container/nginx.conf`, the template in `/etc/nginx/templates/`, `ENV NGINX_ENVSUBST_OUTPUT_DIR=/tmp/nginx/conf.d NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 SOCALYTICS_OBJECT_STORAGE_ORIGIN=`, `EXPOSE 8080`, `USER 101`, no `HEALTHCHECK`, and the labels of contracts/container-images.md §2 with title `socalytics-web` (research R3, R4; FR-001 to FR-013, FR-019, FR-028)
- [x] T011 [P] [US3] Add `src/clients/web/tests/unit/build/securityHeaders.test.ts`: the policy rendered by `src/clients/web/build/securityHeaders.ts` without an origin equals the `Content-Security-Policy` value in `src/clients/web/container/default.conf.template` with the `${SOCALYTICS_OBJECT_STORAGE_ORIGIN}` placeholder removed, the policy with an origin adds it to `connect-src` only, and the template sets the three common headers with the module's values (research R5; FR-022)

### Container evidence for User Story 3

- [x] T012 [US3] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/WebUiImageTests.cs` using `StampDependenciesFixture` and a running API container: start the Web UI image read-only with `/tmp` tmpfs, `SOCALYTICS_API_UPSTREAM` pointing at the API alias, and a test object storage origin; assert every file in `/shell-manifest.json` is served with a body whose SHA-384 equals its `integrity`, every `<script>` and stylesheet `integrity` attribute in `/index.html` matches the served asset, `/sw.js` contains the release version, the contract's `Cache-Control` values, 404 for a missing `/assets/` file, `index.html` for a client route, `application/manifest+json` for the web app manifest, and the exact §6.3 headers (with and without the object storage origin); assert `/alive` and an anonymous `/api/v1` request through the Web UI origin reach the API and return `SocAlytics-Minimum-Web-Version`; start a second Web UI container with an unreachable upstream and assert the shell is served and `/api/v1/…` answers 502; assert the process UID is 101 and `ReadonlyRootfs` is true, and that a Web UI container with an arbitrary non-root user (for example `12345:0`) also serves the shell (spec US3 scenarios 1 to 6; SC-004, SC-005)

**Checkpoint**: The Web UI image serves the release on one origin with the API

---

## Phase 5: User Story 4 - Trace and trust what is inside an image (Priority: P2)

**Goal**: The three images carry identical provenance labels and contain no secret, development, or test material

**Independent Test**: Read the labels of the three built images and scan every layer and the image configuration

### Tests for User Story 4

- [X] T013 [US4] Add `src/platform/Tests/SocAlytics.Platform.Images.Tests/Support/LayerScanner.cs` (reads `docker save` output and lists every layer's files and the image configuration) and `src/platform/Tests/SocAlytics.Platform.Images.Tests/ImageHardeningTests.cs`: for each of the three images assert the labels of contracts/container-images.md §2 with the fixture's revision and version (identical across the images) and the Web UI's `/shell-manifest.json` version equal to the version label; assert no `Healthcheck` in any image configuration and a non-root numeric `User` (1654, 1654, 101); assert no layer contains private-key PEM blocks, `*.pfx`, `*.p12`, `*.key`, ASP.NET Core development certificates, `secrets.json` or `UserSecrets` folders, `appsettings.Development.json`, `.env*` files, `.git`, `*.Tests.dll`, test fixtures, or (in the Web UI image) `node_modules`, ignoring only the base image's system CA trust store; assert the configuration's environment, labels, and history contain no credential-like values; run `docker build` without `SOCALYTICS_REVISION`, without `SOCALYTICS_VERSION`, and with a malformed version for each Dockerfile and assert the build fails with a message naming the argument (spec US4 scenarios 1 to 3; FR-004, FR-005, FR-011, FR-028; SC-006, SC-007)

**Checkpoint**: Provenance and the absence of secret material are proven for every image

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and the final validation

- [x] T014 [P] Document the image builds and their runtime contract: add a "Container images" subsection under "Development" in `README.md` with the three `docker build` commands of quickstart.md (required arguments, PowerShell revision and version), the evidence command `dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests`, and the statement that the images are not deployment support or production readiness; add the API and Migrator image commands, users, port, writable `/tmp`, and configuration surface to `src/platform/README.md`; add the Web UI image, its `SOCALYTICS_API_UPSTREAM` and `SOCALYTICS_OBJECT_STORAGE_ORIGIN` variables, and the one-origin forwarding contract to `src/clients/README.md` (replacing "No … container image … exists yet"); update `AGENTS.md` ("Current State": the three images and their evidence project; "Repository Setup": the build commands and that image tests need Docker) (FR-006, FR-025)
- [x] T015 [P] Make `docs/architecture/` describe the merged state (plan.md "Architecture Updates"): re-read `docs/architecture/` at implementation time, including summaries, introductions, and evidence lists, and correct every statement the merged images make false, for example in `docs/architecture/platform-implementation.md` (Source And Runtime Baseline, Client Technology, Architecture Reassessment), `docs/architecture/client-applications.md` (Source Ownership And Delivery), `docs/architecture/production-operations.md` (portability rules), `docs/architecture/overview.md` (Implementation Profile), and `docs/architecture/security-and-data-governance.md` (the Web UI policy's production server configuration); turn the design paragraphs worded as decided but not implemented into present tense, matching what was built; keep production profile values, registries, publication, Compose, and Azure Container Apps explicitly unresolved or future, and never present the images as deployment support or production readiness (constitution Principle I)
- [x] T016 Run the validation of `specs/20261018-092503-oci-images/quickstart.md`: the three documented `docker build` commands, `dotnet test src/platform/Tests/SocAlytics.Platform.Images.Tests`, the Web UI `npm run build`, `npm test`, and `npm run test:e2e` in `src/clients/web`, and `node .github/scripts/check-markdown.mjs`; fix any failure in the files of the task that introduced it

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: No dependencies; blocks all stories
- **US2 Migrator (Phase 2)**: Depends on Phase 1
- **US1 API (Phase 3)**: Depends on Phase 2 (the API evidence prepares its database with the Migrator image)
- **US3 Web UI (Phase 4)**: Depends on Phase 3 (forwarding is proven against the API image)
- **US4 Provenance (Phase 5)**: Depends on Phases 2 to 4 (scans all three images)
- **Polish (Phase 6)**: Depends on all stories

### Within Each Story

- The image definition lands before the tests that build it
- Support fixtures land before the tests that use them

### Parallel Opportunities

- Phase 1: T001, T002, and T003 change disjoint files
- Phase 3: T006 (Dockerfile) and T007 (dependency fixture)
- Phase 4: T009 (server configuration) alone, because the Dockerfile and the policy test use its files; then T010
  (Dockerfile) and T011 (Vitest); then T012 (container test), which builds both
- Phase 6: T014 (READMEs and `AGENTS.md`) and T015 (`docs/architecture/`)

---

## Parallel Example: User Story 3

```text
Task: "T009 Add src/clients/web/container/nginx.conf and default.conf.template"
# then
Task: "T010 Add src/clients/web/Dockerfile and Dockerfile.dockerignore"
Task: "T011 Add src/clients/web/tests/unit/build/securityHeaders.test.ts"
# then
Task: "T012 Add src/platform/Tests/SocAlytics.Platform.Images.Tests/WebUiImageTests.cs"
```

---

## Implementation Strategy

### MVP First (User Stories 2 and 1)

1. Complete Phase 1: Foundational
2. Complete Phase 2 (Migrator image) and Phase 3 (API image)
3. **STOP and VALIDATE**: the API image reaches healthy against a database prepared by the Migrator image

### Incremental Delivery

1. Foundation → Migrator image → API image (MVP)
2. Web UI image on one origin
3. Provenance and secret-material evidence across all images
4. Documentation and architecture for the merged state

---

## Notes

- [P] tasks change different files and have no dependencies on each other
- Image tests build each image once per run; they need Docker, as the other container-backed tests do
- Never change `.github/`, the AppHost composition, or any production profile value

# Research: Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI

All decisions build on the existing projects (see [plan.md](plan.md)). Values that belong to a production profile
(registries, host architecture, ports exposed to users, TLS, secrets, replica counts) stay **Open / Blocking** as
[Production Deployment and Operations](../../../docs/architecture/production-operations.md#versioned-production-profile)
defines.

## R1 Build tool and build context

- **Decision**: One `Dockerfile` per deployable, built with the Docker CLI (`docker build`, BuildKit) from the
  repository root as build context: `src/platform/SocAlytics.Platform.Api/Dockerfile`,
  `src/platform/SocAlytics.Platform.Migrator/Dockerfile`, and `src/clients/web/Dockerfile`. Each has a
  Dockerfile-specific ignore file next to it (`Dockerfile.dockerignore`) that excludes everything (`*`) and re-includes
  only what that image needs (the platform projects, `src/platform/global.json`, `Directory.*.props`, the
  repository-root `contracts/` schemas the Infrastructure project embeds, and for the Web UI the `src/clients/web`
  sources, lockfile, and the OpenAPI snapshot); `bin/`, `obj/`, `node_modules/`, `dist/`, `src/generated/`,
  `test-results/`, test projects, `.git`, `.env*`, and `appsettings.Development.json` stay excluded.
- **Rationale**: The Infrastructure project embeds `contracts/**/*.schema.json` from the repository root and the Web UI
  build reads the API's OpenAPI snapshot, so the context must be the repository root. Dockerfile-specific ignore files
  keep every image's allow-list next to its Dockerfile inside a covered source scope, so no repository-root file is
  needed (a root `.dockerignore` would not be covered by any verification check and would hold the implementation for
  review). The documented command and the evidence use the same CLI, so the evidence builds exactly what operators build.
- **Alternatives considered**: Testcontainers' `ImageFromDockerfileBuilder` (used for the test Analyst image) sends its
  own tar context and does not honor Dockerfile-specific ignore files; a repository-root `.dockerignore` (shared by all
  images, uncovered by verification); building outside Docker and copying outputs into thin images (not reproducible
  from the definition alone).

## R2 .NET base images and publish

- **Decision**: Build stage `mcr.microsoft.com/dotnet/sdk` with an exact SDK version tag in the feature band of
  `src/platform/global.json` (10.0.4xx), pinned by digest; runtime stages
  `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra` (API) and
  `mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra` (Migrator), both pinned by digest. `dotnet publish -c
  Release` of the single project, framework-dependent, `UseAppHost=false`, into `/app`; the entry point is
  `dotnet <Assembly>.dll`. The API project marks `appsettings.Development.json` `CopyToPublishDirectory="Never"`.
- **Rationale**: Chiseled images contain no shell or package manager and run as the non-root `app` user (UID 1654) by
  default; the `-extra` variants keep ICU and time-zone data, so globalization behavior equals the current host's.
  Publishing only the API project leaves the Migrator and DbUp out of the API image (FR-015); publishing only the
  Migrator leaves the API host out of the Migrator image (FR-018). The migration SQL embedded in the Infrastructure
  assembly is data, not an execution path: without the Migrator and DbUp nothing in the API image can apply it.
- **Alternatives considered**: Full Debian/Ubuntu runtime images (shell and package manager widen the attack surface);
  self-contained or native AOT publish (not supported by every dependency, larger diff); Alpine (musl differences not
  exercised by current tests).

## R3 Web UI build stages

- **Decision**: Stage 1 (`dotnet/sdk`, same pinned digest as R2) installs Kiota 1.36.0 as a local tool and generates the
  TypeScript client from `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with the arguments of the existing
  `generate` script. Stage 2 (`node:24-bookworm-slim`, pinned by digest) runs `npm ci` with
  `PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1`, copies the generated client, and runs a new npm script `build:bundle`
  (`tsc -b && vite build`), which the existing `build` script then calls after `generate`, with
  `SOCALYTICS_WEB_VERSION` set from the release version. Stage 3 is the web server (R4) with only `dist/`.
- **Rationale**: Splitting `build` into `generate` and `build:bundle` lets the container build run the same steps as the
  documented local build without needing Kiota in the Node image; the version stamps the shell exactly as the archived
  PWA spec defines, so the image version label and the shell's reported version are the same value.
- **Alternatives considered**: Installing .NET in the Node stage (larger, two toolchains in one stage); committing the
  generated client (forbidden: `src/generated/` is ignored and regenerated from the snapshot).

## R4 Web UI web server and one origin

- **Decision**: `nginxinc/nginx-unprivileged` (Alpine variant) pinned by digest, running as UID 101 on port 8080. The
  image ships its own `nginx.conf` and one server template; at start the image's existing entrypoint renders the
  template with `envsubst` into `/tmp` (`NGINX_ENVSUBST_OUTPUT_DIR`), and all nginx temporary paths and the PID file
  live in `/tmp`. The template forwards `/api`, `/api/…`, and `/alive` (the paths the AppHost's Vite proxy forwards) to
  `SOCALYTICS_API_UPSTREAM`, resolved per request through the container's resolvers
  (`NGINX_ENTRYPOINT_LOCAL_RESOLVERS`), so nginx starts when the API is unreachable or not yet resolvable and answers
  forwarded paths with 502 (FR-024). It adds `X-Forwarded-For`, `X-Forwarded-Proto`, and `X-Forwarded-Host`, passes the
  request unchanged otherwise, disables request and response buffering, sets no body-size limit
  (`client_max_body_size 0`), and uses one-hour read and send timeouts. The Web UI image itself provides the one origin
  (clarification); a profile may still put its own ingress in front.
- **Rationale**: The official unprivileged image already supports non-root, read-only roots with a writable `/tmp`, and
  runtime templating without a custom entrypoint; forwarding in the Web UI image gives both deployment profiles one
  origin from two images without a third component.
- **Alternatives considered**: Caddy (equally capable, but runs as root by default and needs XDG directories moved);
  an external reverse proxy in every profile (adds a component and duplicates the forwarding contract); serving the
  Web UI from the API (contradicts the separate-container decision).

## R5 Static serving rules and security headers

- **Decision**: `index.html`, `sw.js`, `shell-manifest.json`, and `manifest.webmanifest` are sent with
  `Cache-Control: no-cache`; files under `/assets/` (content-fingerprinted by Vite) with
  `Cache-Control: public, max-age=31536000, immutable`; other static files with `no-cache`. Requests for paths with a
  file extension or under `/assets/` that do not exist return 404; other paths return `index.html` (client routes).
  `.webmanifest` is served as `application/manifest+json`. Every response the server produces itself carries the
  production Content Security Policy and `X-Content-Type-Options`, `Referrer-Policy`, and `Permissions-Policy` from the
  [security-header contract](../20261012-150657-web-ui-foundation/contracts/security-headers.md) as extended
  by the [PWA contract](../20261015-194113-web-ui-pwa-updates/contracts/web-ui-pwa.md); `connect-src` adds
  `SOCALYTICS_OBJECT_STORAGE_ORIGIN` when set (the same variable `vite.config.ts` reads). Forwarded API responses keep
  the API's own headers. The production policy moves into one module (`src/clients/web/build/securityHeaders.ts`) used
  by `vite.config.ts`, and a Vitest test fails when the server template's policy differs from it.
- **Rationale**: The worker fetches `sw.js` with `updateViaCache: 'none'` and reads `shell-manifest.json` on install, so
  both must revalidate; a missing asset answered with `index.html` would make an integrity failure look like a content
  mismatch. One policy source keeps preview mode, which the existing browser tests measure for violations, and the
  image identical.
- **Alternatives considered**: Generating the server configuration from the module at build time (more machinery for
  the same guarantee); `<meta>` CSP (cannot express `frame-ancestors`).

## R6 Read-only root, writable location, users, ports

- **Decision**: All three images run with a read-only root filesystem and one writable tmpfs at `/tmp` (`TMPDIR=/tmp`).
  Users: API and Migrator UID 1654 (`app`), Web UI UID 101; ports: API 8080 (`ASPNETCORE_HTTP_PORTS=8080`), Web UI
  8080, Migrator none. No image declares `HEALTHCHECK` (clarification). Files in the images are owned by root and
  readable by others, so an orchestrator-assigned non-root UID also works.
- **Rationale**: The .NET diagnostics IPC socket and nginx temporary files need a writable directory; one documented
  location keeps every profile's mount simple. No component persists the ASP.NET Core Data Protection key ring today;
  the read-only evidence fails if anything writes outside `/tmp`.
- **Alternatives considered**: Several writable mounts per image; disabling .NET diagnostics instead of a writable
  `/tmp` (hides a useful operational feature).

## R7 Release version, revision, and labels

- **Decision**: Required build arguments `SOCALYTICS_REVISION` (full commit hash) and `SOCALYTICS_VERSION`
  (`YYYY.MMDD.HHMMSS`); the build stage of each Dockerfile fails with a message naming the missing or malformed
  argument. Labels: `org.opencontainers.image.revision`, `org.opencontainers.image.version`,
  `org.opencontainers.image.source` (`https://github.com/markusheiliger/socalytics`), `org.opencontainers.image.title`,
  `org.opencontainers.image.description`. No `created` label, so labels depend only on inputs.
- **Rationale**: One release version in the Web UI's existing sortable shape identifies the three images of a release
  (clarification) and is exactly what `WebUi:MinimumVersion` compares against.
- **Alternatives considered**: Semantic versions for the .NET images and a separate Web UI version (two version
  schemes for one release); defaults such as `0.0.0-local` (unlabeled images could reach a stamp).

## R8 Evidence design

- **Decision**: A new test project `src/platform/Tests/SocAlytics.Platform.Images.Tests` (xUnit v3, Shouldly,
  Testcontainers, referencing `SocAlytics.Platform.Integration.Tests` for its container fixtures' configuration helpers,
  NATS JWT trust, and the roles initialization script). One collection fixture builds the three images once per run
  with the documented `docker build` commands (process invocation, unique tags, a fixed test revision and version) and
  removes them afterwards. The API, Migrator, and Web UI containers and their disposable dependencies (PostgreSQL with
  the roles script, NATS with JWT trust, RustFS) share one Testcontainers network with aliases. Before the API starts,
  the test runs the Migrator image and then the existing configured-workflow seeding in-process (Test environment)
  against the same database, so the API container runs unchanged in its production environment (clarification). SMTP
  options name a host that is never contacted. Checks: image configuration (user, labels, no `Healthcheck`), container
  inspection (`ReadonlyRootfs`, process UID through the Docker Engine `top` operation), HTTP probes, Migrator exit codes,
  an exported filesystem listing for the API and Migrator images, HTTP-level Web UI checks (every shell-manifest file
  served byte for byte with its SHA-384 integrity, `index.html` integrity attributes, headers equal to the contract,
  caching, fallback, MIME types, forwarding through the origin including `SocAlytics-Minimum-Web-Version`, 502 when the
  upstream is unreachable), and a layer scan of `docker save` output (forbidden paths and private-key, certificate
  bundle files other than the base image's system trust store, user-secrets, development settings, environment files,
  `.git`, and test assemblies in layers, environment, labels, and history).
- **Rationale**: Reusing the existing fixtures and Docker keeps the evidence within what `environment-setup` already
  provides (Docker on the runner, .NET, Node); browser-level absence of CSP violations for this exact policy is
  already measured by the existing Playwright tests against `vite preview`, and R5 makes the image's policy identical by
  test, so the image evidence checks it at the HTTP level.
- **Alternatives considered**: A Playwright project in the Web UI that starts containers (would build the API image on
  every Web UI verification); Playwright for .NET (a new tool the environment does not install).
- **Refinement (diagnosis of the T003 stop)**: The Images test project needs `InternalsVisibleTo` from the Infrastructure project for the in-process seeding. `PersistenceArchitectureTests` pins the exact set of assemblies that may see Infrastructure internals (Migrator, Integration, EndToEnd), so T003 failed that test on both attempts (`Images.Tests` unexpected). Decision: T003 extends the expected set by this one evidence assembly in the same task. Rejected: dropping `InternalsVisibleTo` (the seeding needs internals) and loosening the test to a subset or prefix check (it would stop guarding the boundary). Evidence: both T003 attempts built the project cleanly and only that architecture test failed.

## R9 Environment check

The plan needs .NET 10 SDK, Node.js 24, and Docker with the BuildKit-based `docker build` CLI; all are provided
(`environment-setup` installs the SDK, the workflow provides Node.js, and Docker is on the runner and already used by the
platform's container-backed tests, which also pull public images). The image builds download base images, NuGet and
npm packages, and Kiota inside Docker, as the existing verification already downloads packages and images. No
environment spec is needed. The Dockerfiles live in the covered platform and Web UI scopes; the image tests run with the
platform tests (every phase that changes `src/platform/` and the final verification).

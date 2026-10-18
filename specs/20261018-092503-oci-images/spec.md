# Feature Specification: Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI

**Feature Branch**: `20261018-092503-oci-images`

**Created**: 2026-10-18

**Status**: Draft

**Input**: User description: "Cloud-Neutral OCI Images for the Platform API, Migrator, and Web UI: the architecture decides that SocAlytics deployables are cloud-neutral OCI images, that the platform API and the Web UI are separate containers (the API image built from `src/platform`, the Web UI image built from `src/clients` and release-coupled to the API), and that the one-off `SocAlytics.Platform.Migrator` ships as its own image and is the only component able to change database structures, while the API image contains no migration execution path (docs/architecture/platform-implementation.md#source-and-runtime-baseline, docs/architecture/client-applications.md#source-ownership-and-delivery: \"the OCI container image … not implemented yet\", docs/architecture/production-operations.md portability rules: both profiles run the same images built from the same release). None exists. This feature adds reproducible container image definitions for the three deployables: multi-stage builds from the existing projects with pinned base images by digest, non-root users, read-only-root-filesystem compatibility, no secrets, credentials, development certificates, user secrets, or test assets in any layer, configuration only through environment variables and mounted files the existing options already read, the existing `/alive` and `/health` contracts for the API, a Migrator that runs once and exits with its existing outcome, and a Web UI image that serves the built static application (including the PWA service worker and integrity-verified shell rules of the archived spec `specs/archive/20261015-194113-web-ui-pwa-updates`) on one origin with the API through a documented reverse-proxy contract or as the plan decides consistent with the one-origin rule; images carry OCI labels for source revision and version; build commands are documented. Automated evidence builds the three images and proves in containers: the API starts and reports alive/health against disposable dependencies, the API image has no migration path, the Migrator applies migrations to a disposable PostgreSQL and exits, the Web UI image serves the shell and passes its integrity rules, processes run as non-root, and image layers contain no secret material. Out of scope: Docker Compose (spec 2), registries, publishing, signing, SBOM/attestation publication and release workflows (implementation automation cannot change `.github/workflows`), Azure Container Apps, production configuration values, TLS, Analyst images, the Analyst Manager. Depends on Platform Persistence Foundation, Web UI Foundation, Installable Web UI with Service Worker Updates (merged; archived under specs/archive/). Implements the image parts of docs/architecture/platform-implementation.md#source-and-runtime-baseline, docs/architecture/client-applications.md#source-ownership-and-delivery, and the portability rules in docs/architecture/production-operations.md (values stay Open / Blocking)."

## Clarifications

### Session 2026-10-18

- Q: Should the Web UI image itself forward the API paths to the API container, or must a separate reverse proxy in front of both containers provide the one origin? → A: The Web UI image forwards the API paths itself to an API address from configuration, so every profile gets one origin from the two images without an extra component; a profile may still place its own ingress in front of the Web UI container.
- Q: Should all three images of a release carry one release version, and in which format? → A: One release version for all three images, in the Web UI's existing sortable shape `YYYY.MMDD.HHMMSS`; every image build rejects other shapes.
- Q: In which hosting environment should the automated evidence run the API container? → A: In the image's default production environment, with every required option supplied as environment variables or mounted files, so the evidence never relies on the relaxed Development or Test checks.
- Q: Should the images contain an in-container health probe command for orchestrators that probe from inside the container? → A: No; the images stay minimal and orchestrators probe `/alive` and `/health` over HTTP; a deployment feature that needs in-container probing decides how.
- Q: Which CPU architectures must the images and their evidence support? → A: The image definitions hard-code no CPU architecture; the evidence builds and runs them for the build host's architecture only, and multi-architecture publication stays with the out-of-scope release work.
- Q: How should the evidence let the production-environment API reach healthy, given that the existing readiness check also requires a resolvable default analysis workflow, which a production stamp has only once the Registry holds one? → A: The evidence arranges the disposable database with a resolvable default analysis workflow after the Migrator ran and before the API starts, as the integration tests arrange data, so the API image runs unchanged in its production environment; the readiness rule itself stays unchanged.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Run the platform API from its image (Priority: P1)

A stamp operator (or a later deployment definition such as the Compose stamp) wants to run the platform API without a
source checkout or an SDK. The operator builds the API image from one source revision, starts it with configuration
supplied only as environment variables and mounted files, points it at the stamp's database, messaging, and object
storage, and probes the existing liveness (`/alive`) and readiness (`/health`) endpoints to decide when the API is
ready. The image runs as an unprivileged user on a read-only root filesystem and cannot change the database structure.

**Why this priority**: The API is the core deployable; every deployment profile in
[Production Deployment and Operations](../../docs/architecture/production-operations.md#azure-container-apps-target-profile-provisional)
runs it, and the Compose stamp composition that follows this feature cannot exist without it.

**Independent Test**: Build the API image, start it against disposable dependencies whose schema was prepared without
the API, and observe that `/alive` reports alive, `/health` reports healthy, the process runs as a non-root user with a
read-only root filesystem, and the image contains no migration execution path.

**Acceptance Scenarios**:

1. **Given** an API image built from one source revision and reachable disposable dependencies with an up-to-date
   database schema and a resolvable default analysis workflow, **When** the container starts with only environment
   variables and mounted files as configuration, **Then** `/alive` reports alive and `/health` reports healthy within
   the readiness budget of SC-002.
2. **Given** the same image and a database that is unreachable, **When** the container starts, **Then** `/alive`
   reports alive while `/health` reports unhealthy, exactly as the existing readiness contract defines.
3. **Given** the API image, **When** its contents are inspected, **Then** it contains no Migrator, no migration
   execution library, and no entry point that applies migrations, and its default process runs as a non-root user.
4. **Given** the container runs with a read-only root filesystem and only the documented writable location, **When**
   it serves requests, **Then** `/alive` and `/health` answer as in scenario 1 and nothing is written outside the
   documented writable location.
5. **Given** required configuration is missing, **When** the container starts, **Then** it stops with the existing
   options-validation failure instead of starting with insecure or development defaults.

---

### User Story 2 - Apply migrations with the Migrator image (Priority: P1)

Before any API replica starts, the deployment runs the Migrator image once against the stamp database. It applies the
pending forward-only migrations, exits with its existing outcome code, and leaves nothing running. It is the only
deployable that receives database credentials able to create or alter data structures.

**Why this priority**: The API image deliberately cannot migrate, so no deployment can reach a usable schema without
the Migrator image; it is as essential as the API image.

**Independent Test**: Run the Migrator image against an empty disposable database, observe exit code 0 and the applied
migrations in the history journal; run it again and observe exit code 0 with nothing applied; run it with invalid
configuration and observe the existing configuration-failure exit code.

**Acceptance Scenarios**:

1. **Given** an empty disposable PostgreSQL database with the migrator and application roles, **When** the Migrator
   container runs, **Then** it applies every migration of its source revision and exits with the success code.
2. **Given** a database already at the image's revision, **When** the Migrator container runs again, **Then** it applies
   nothing and exits with the success code.
3. **Given** missing or invalid configuration, or an unreachable database, **When** the Migrator container runs,
   **Then** it exits with the existing configuration or database-unavailable code without retrying forever.
4. **Given** the Migrator image, **When** it runs, **Then** its process runs as a non-root user on a read-only root
   filesystem and the container ends after one run.

---

### User Story 3 - Serve the Web UI from its image on one origin with the API (Priority: P2)

A user opens SocAlytics in a browser. The Web UI image serves the built, installable Web UI of one release, including
its service worker, shell manifest, and integrity-verified shell files, with the production security headers. Requests
the browser sends to the platform API on the same origin reach the API container, so sessions, anti-forgery, and the
service worker work exactly as in local development.

**Why this priority**: The Web UI is how members use SocAlytics, but it depends on a running API image to be useful; it
follows the API and Migrator images.

**Independent Test**: Build the Web UI image, start it with the API image behind it, and observe through its origin that
every shell file is served exactly as built so it passes the integrity check the service worker applies on install,
the security headers match the Web UI security-header and PWA contracts and equal the policy that the existing browser
tests measure for violations, and an API request through the origin reaches the API.

**Acceptance Scenarios**:

1. **Given** the Web UI image and a running API container, **When** the origin is requested, **Then** the shell is
   served, every shell file listed in the shell manifest matches its SHA-384 integrity, and the Content Security Policy
   equals the production policy that the existing browser tests prove free of violations.
2. **Given** the same origin, **When** the page calls the API paths that the local development composition forwards,
   **Then** the requests reach the API container unchanged except for added forwarding headers, and responses,
   including the minimum Web UI version header, reach the browser unchanged.
3. **Given** a navigation to any client route, **When** the request reaches the Web UI container, **Then** it returns the
   application shell, while a request for a missing static file returns not found rather than the shell.
4. **Given** the service worker script, the shell manifest, and the HTML shell, **When** they are requested, **Then** they
   are served so that browsers revalidate them on every update check, while fingerprinted assets may be cached long-term.
5. **Given** the API container is unreachable, **When** the Web UI container starts, **Then** it still serves the shell and
   answers API paths with a gateway error, so the existing connection-required state appears.
6. **Given** the Web UI image, **When** it runs, **Then** its processes run as a non-root user on a read-only root
   filesystem.

---

### User Story 4 - Trace and trust what is inside an image (Priority: P2)

A release reviewer or stamp operator needs to know which source revision and release version an image was built from,
and needs confidence that no image carries credentials or development or test material. Each image carries standard
OCI labels for its source revision and version, the three images of one release carry the same revision and version,
and automated evidence inspects every layer for secret material.

**Why this priority**: Provenance and the absence of secrets are preconditions for any later registry, signing, or
release work, but they add no runtime behavior.

**Independent Test**: Build the three images with one revision and version, read their labels, and run the layer
inspection that fails on any secret material, development certificate, user-secrets store, development settings file,
or test asset.

**Acceptance Scenarios**:

1. **Given** the three images built from one revision with one version, **When** their labels are read, **Then** each
   carries the same source revision and version labels and the Web UI image's version equals the release version its
   shell reports.
2. **Given** any of the three images, **When** every layer and the image configuration are inspected, **Then** none
   contains private keys, certificates other than the base image's public CA trust store, passwords, tokens, user
   secrets, development settings files, `.env` files, source-control metadata, or test assemblies and fixtures.
3. **Given** a build without a source revision or version, **When** the documented build runs, **Then** it fails with a
   message naming the missing input instead of producing an unlabeled image.

### Edge Cases

- A base image tag moves upstream: builds keep using the pinned digest and are unaffected until the pin is changed.
- The build context contains local developer files (build outputs, user secrets, `.env`, `node_modules`, test
  results): they never reach any image layer.
- The API starts before the Migrator finished: `/health` reports unhealthy because the migration state is not current,
  exactly as the existing readiness check defines; the API never applies migrations itself.
- The production-environment API runs against a stamp whose Registry holds no resolvable default analysis workflow:
  `/health` stays unhealthy under the existing readiness rule until one exists; the images do not change that rule.
- Two Migrator containers start at the same time: the existing migration lock admits one and the other waits within
  its bounded wait or exits with the lock-timeout code.
- The Migrator container is stopped mid-run: it exits with the existing cancelled outcome; a later run continues.
- The orchestrator assigns an arbitrary non-root user ID: the images still start, because they need no write access
  outside the documented writable location.
- The writable temporary location is not mounted on a read-only root filesystem: the deployment does not meet the
  image contract, which requires it; the evidence always runs the images with it.
- A request body larger than the proxy would buffer by default (for example a result manifest posted through the one
  origin): the Web UI container forwards it without imposing a smaller limit than the API's own.
- The object storage origin is not configured for the Web UI container: the Content Security Policy allows only
  same-origin connections, as in the existing preview configuration.

## Requirements *(mandatory)*

### Functional Requirements

#### Image definitions and builds

- **FR-001**: The repository MUST contain one image definition for each of the three deployables: the platform API
  (built from `src/platform`), the Migrator (built from `src/platform`), and the Web UI (built from `src/clients`).
- **FR-002**: Each image MUST be built in stages from the existing projects of its source revision, so that build tools,
  SDKs, package caches, and intermediate outputs stay out of the final image.
- **FR-003**: Every base image MUST be pinned by an immutable digest, and every tool or package the build downloads
  MUST come from a pinned version or the committed lockfiles, so that one revision and the same build inputs always
  select the same dependencies. The image definitions MUST NOT hard-code a CPU architecture.
- **FR-004**: The build MUST require a source revision and a release version as inputs and MUST fail when either is
  missing; the release version MUST have the Web UI's sortable release-version shape `YYYY.MMDD.HHMMSS`, every image
  build MUST reject other shapes, and the three images of a release MUST be built with the same revision and version.
- **FR-005**: Each image MUST carry the standard OCI labels for source revision, version, source location, title, and
  description, with the revision and version taken from the build inputs.
- **FR-006**: The build commands for all three images MUST be documented with the repository's other supported
  commands, including the inputs they require.
- **FR-007**: The build context MUST exclude everything the image does not need, in particular build outputs, local
  dependency folders, user secrets, environment files, test projects and results, and source-control metadata.

#### Runtime behavior common to all images

- **FR-008**: Every image MUST run its process as a fixed non-root numeric user by default and MUST NOT require root at
  start or at run time.
- **FR-009**: Every image MUST run unchanged on a read-only root filesystem with a single documented writable temporary
  location, and MUST NOT write anywhere else.
- **FR-010**: Every image MUST listen only on unprivileged ports, documented per image.
- **FR-011**: Image layers and image configuration MUST NOT contain secrets, credentials, private keys, development
  certificates, user secrets, development-only settings files, environment files, source-control metadata, or test
  assets.
- **FR-012**: The images MUST receive all configuration only through environment variables and mounted files read by the
  existing configuration and options, MUST NOT contain stamp-, profile-, or cloud-specific values, and the API and
  Migrator images MUST start in the production hosting environment by default.
- **FR-013**: The images MUST take no dependency that only one deployment profile can satisfy, so that the Compose
  profile and the Azure Container Apps target profile can run the same images.
- **FR-028**: Images MUST NOT declare an in-container health probe; the API's health is probed over HTTP through
  `/alive` and `/health` only.

#### Platform API image

- **FR-014**: The API image MUST run the existing API host and expose the existing `/alive` liveness and `/health`
  readiness endpoints with their existing meanings.
- **FR-015**: The API image MUST contain no Migrator, no migration execution library, and no entry point that applies
  migrations.
- **FR-016**: The API image MUST validate its configuration with the existing options validation at start and stop with
  a failure when it is invalid.

#### Migrator image

- **FR-017**: The Migrator image MUST run the existing Migrator once and exit with the existing Migrator outcome code;
  it MUST NOT restart itself or keep running after the run.
- **FR-018**: The Migrator image MUST contain only what the Migrator needs and MUST NOT contain the API host.

#### Web UI image

- **FR-019**: The Web UI image MUST serve the Web UI built from the same revision, including the service worker at its
  fixed URL, the shell manifest, the web app manifest, icons, and every shell file listed in the shell manifest, byte
  for byte as built, so that every shell file passes its integrity check.
- **FR-020**: The Web UI image MUST answer client-route navigations with the HTML shell and requests for missing static
  files with not found.
- **FR-021**: The Web UI image MUST serve the HTML shell, the service worker, and the shell manifest so that browsers
  revalidate them on each request, and MAY allow long-term caching only for content-fingerprinted files.
- **FR-022**: The Web UI image MUST send the production Content Security Policy and the other security headers of the
  Web UI security-header and PWA contracts on every response it serves itself; the only runtime-configurable part of the
  policy is the object storage origin allowed for direct uploads.
- **FR-023**: The Web UI image itself MUST make the platform API reachable on the same origin, without a separate proxy
  component, by forwarding exactly the API paths that the local development composition forwards to an API address supplied as configuration, passing method,
  path, query, headers, and body unchanged except for added standard forwarding headers, streaming request and response
  bodies, and imposing no request-size or timeout limit smaller than the API's own.
- **FR-024**: The Web UI image MUST start and serve the shell even when the API is unreachable, answering forwarded paths
  with a gateway error.
- **FR-025**: The one-origin forwarding contract (forwarded paths, forwarding headers, configuration inputs, limits) MUST
  be documented so that every deployment profile can rely on it.

#### Evidence

- **FR-026**: Automated tests MUST build the three images from the working tree with the documented build commands and
  prove in containers, for the build host's CPU architecture: the API, started in its default production hosting
  environment with every required option supplied as environment variables or mounted files, reports alive and healthy
  against disposable dependencies whose database the evidence prepared with the Migrator image and a resolvable default
  analysis workflow; the API image contains no
  migration execution path; the Migrator applies migrations to a disposable PostgreSQL, is idempotent on a second run,
  and exits with the existing failure code for invalid configuration; the Web UI image serves the shell with every
  shell file matching the integrity its service worker verifies, its headers match the contracts and its policy equals
  the one the existing browser tests measure, and an API request through
  its origin reaches the API; every container runs as a non-root user on a read-only root filesystem; and no image layer
  or image configuration contains secret material.
- **FR-027**: The evidence MUST use only disposable containers and MUST fail visibly when no container runtime is
  available, as the repository's other container-backed tests do.

### Key Entities

- **Deployable image**: one of the three images (API, Migrator, Web UI); has a source revision, a release version, a
  base image digest, a non-root user, its ports, its writable location, and its configuration inputs.
- **Release**: the set of the three images built from one source revision with one release version; the Web UI image of
  a release is coupled to its API image.
- **One-origin forwarding contract**: the API paths the Web UI image forwards, the forwarding headers it adds, the API
  address it reads from configuration, and the limits it does not impose.
- **Image provenance labels**: the OCI labels that record source revision, version, source location, title, and
  description.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All three images build from a clean checkout with the documented commands, with no step that needs
  credentials, development certificates, or user secrets.
- **SC-002**: A started API container reports alive within 30 seconds and healthy within 60 seconds against reachable
  disposable dependencies with a current schema and a resolvable default analysis workflow.
- **SC-003**: The Migrator container applies all migrations to an empty disposable database and exits with the success
  code in one run, and a second run exits with the success code with zero migrations applied.
- **SC-004**: 100% of the shell files listed in the shell manifest are served by the Web UI image with a body that
  matches their integrity, and the image's Content Security Policy is identical to the production policy for which the
  existing browser tests record zero violations.
- **SC-005**: 100% of the evidence containers run as a non-root user on a read-only root filesystem.
- **SC-006**: The layer inspection finds zero secret-material, development-settings, user-secrets, or test-asset files
  across all layers of all three images.
- **SC-007**: The three images of one build carry identical source-revision and version labels.

## Assumptions

- Dependencies: Platform Persistence Foundation
  ([archived](../archive/20261005-130700-platform-persistence-foundation/spec.md)), Web UI Foundation
  ([archived](../archive/20261012-150657-web-ui-foundation/spec.md)), and Installable Web UI with Service Worker
  Updates ([archived](../archive/20261015-194113-web-ui-pwa-updates/spec.md)) are merged and provide the Migrator, the
  API host, the readiness contract, the Web UI build, and the shell integrity rules this feature packages.
- The Compose stamp composition, registries, publishing, signing, SBOM and attestation publication, release workflows,
  Azure Container Apps, production configuration values, TLS termination, Analyst images, and the Analyst Manager are
  out of scope; production profile values stay **Open / Blocking** as
  [Production Deployment and Operations](../../docs/architecture/production-operations.md#versioned-production-profile)
  defines.
- "Reproducible" means pinned inputs (base image digests, lockfiles, pinned tool versions) so that one revision selects
  the same dependencies; bit-for-bit identical image digests across builds are not required.
- The images define no in-container health probe command (FR-028); orchestrators probe `/alive` and `/health` over
  HTTP, and any in-container probing a deployment definition needs is decided by that deployment's feature.
- Multi-architecture builds and publication belong to the out-of-scope release work; the evidence covers the build
  host's CPU architecture.
- TLS is terminated outside these images; the API honors forwarding headers only when its existing configuration
  enables them.
- Release coupling between the Web UI and the API is expressed by building both from one revision with one release
  version; the API's existing minimum Web UI version setting stays deployment configuration.

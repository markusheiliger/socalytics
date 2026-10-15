# Research: Docker Desktop Runtime Adapter for Analyst Execution

All Technical Context unknowns are resolved below. Each entry records the
decision, its rationale, and the alternatives considered.

## R1 — Engine API client

- **Decision**: A small, Manager-owned Docker Engine API client in
  `SocAlytics.Analysts.Manager.Core/Runtime/Docker/` built on `HttpClient` with
  a `SocketsHttpHandler.ConnectCallback` that opens the local endpoint (Windows
  named pipe through `NamedPipeClientStream`, or a Unix domain socket in
  validation configurations). It calls only the endpoints the adapter needs:
  `/_ping`, `/version`, `/info`, `/images/create`, `/images/{digest}/json`,
  `/images/{digest}` (DELETE), `/containers/create`, `/containers/{id}/start`,
  `/containers/{id}/wait`, `/containers/{id}/json`, `/containers/{id}/logs`,
  `/containers/{id}/stop`, `/containers/{id}` (DELETE), and `/containers/json`
  with label filters. The API version is negotiated once from `/version`
  (minimum `1.43`) and pinned in the request path.
- **Rationale**: The adapter needs about fifteen calls. A thin client adds no
  production dependency (constitution Principle V), keeps the endpoint policy
  (R2) under the Manager's control, and is testable with a fake
  `HttpMessageHandler`.
- **Alternatives considered**: `Docker.DotNet` (the original package is no
  longer maintained; the maintained fork is pulled in by Testcontainers and
  stays a test-only dependency); shelling out to the `docker` CLI (adds a host
  tool, string parsing, and a second trust boundary).

## R2 — Runtime endpoint policy

- **Decision**: The profile id is `docker-desktop`. `Runtime:Docker:Endpoint`
  defaults to `npipe://./pipe/docker_engine`. `npipe://` is accepted on Windows
  only. `unix://` is accepted only when the host environment is Development or
  Test (validation configurations), mirroring how the simulated runtime is
  restricted. `tcp://`, `http://`, `https://`, and every other scheme are
  refused before any connection, and the endpoint check fails with remediation.
- **Rationale**: The architecture makes the runtime endpoint a privileged local
  boundary and forbids unauthenticated remote TCP (FR-002); the clarification
  allows the Linux socket only for automated verification.
- **Alternatives considered**: Allowing TLS-protected remote endpoints (out of
  scope and against "local access is the default"); a separate test-only
  adapter (would leave the shipped adapter untested end to end).

## R3 — Preflight probe mapping and compatibility

- **Decision**: `ProbeAsync` maps `/version` (`Platform.Name`, `Version`,
  `ApiVersion`, `Os`, `Arch`) and `/info` (`OSType`, `OperatingSystem`, `NCPU`,
  `MemTotal`) to `RuntimeProbe`: `RuntimeProduct` from `Platform.Name` (for
  example `Docker Desktop 4.x.y`) normalized to its product name,
  `RuntimeVersion` the engine version, `Backend` the daemon `OSType` (`linux`),
  `Os`/`OsVersion`/`Architecture` from the host. Registry authentication and
  image platform are probed by pulling `Runtime:ProbeImage` (a digest in the
  stamp's registry) with the platform-issued pull credential (R4) and reading
  its `Os`/`Architecture`. Resource feasibility compares the configured
  `Runtime:Docker:Cpus` and `Runtime:Docker:MemoryBytes` against `NCPU` and
  `MemTotal`. `MaxParallelAttempts` is the number of configured attempt slots
  that fit those limits. The embedded `runtime-compatibility.json` gains a
  `docker-desktop` entry only for the Docker Desktop versions verified on
  Windows (quickstart § Windows); tests inject their own list through
    dependency injection, and Development builds may add entries from a local
    file named by `Runtime:CompatibilityListPath` (refused outside Development
    and Test), so the Linux engine is never a shipped entry and a person can
    verify Docker Desktop before its entry ships.
- **Rationale**: Support is decided only by the compatibility list (FR-004),
  and the list must contain only verified combinations.
- **Alternatives considered**: Treating any Docker Engine as supported
  (contradicts "detecting a runtime does not make the profile supported").

## R4 — Registry pull credentials

- **Decision**: New Manager self endpoint
  `POST /api/v1/analyst-managers/self/registry-credentials` (DPoP, Analyst
  Manager policy, Active Managers only). The platform resolves the stamp's
  default workflow, collects the distinct image repositories of its nodes
  (all inside `Registry:Images:RegistryHost` and `RepositoryPrefix`), and asks
  the registry's token service for a pull-scoped bearer token
  (`repository:<repo>:pull` for each repository) with its protected read
  credentials, exactly as `OciDistributionClient` already authenticates its own
  reads. It returns `{ registry, repositories, kind: "bearer", token,
  expiresAt }`, or `{ kind: "anonymous" }` when the registry issues no
  challenge (the disposable test registry). The lifetime is the registry
  token's own expiry, capped by `Registry:Images:PullCredentialLifetime`
  (default 5 minutes, configurable because `GOV-CRED-004` is unresolved). The
  Manager passes the token to the Engine API as `X-Registry-Auth` with
  `registrytoken`, keeps it in memory only, and renews it before expiry.
- **Rationale**: Authenticated pulls avoid anonymous rate limits, the
  platform's long-lived GHCR credential never leaves the platform, and the
  token is limited to the workflow's repositories (FR-021–FR-023).
- **Alternatives considered**: Handing the configured GHCR token to Managers
  (durable secret on contributed hosts); per-Manager GitHub App installation
  tokens (needs a GitHub App and cannot be narrowed below repository access).

## R5 — Workflow capabilities for the offer

- **Decision**: New Manager self endpoint
  `GET /api/v1/analyst-managers/self/workflow-capabilities` returning, for each
  node of the resolved default workflow, `{ id, version, accelerators, cpus,
  memoryBytes }` from its approved Analyst profile. The adapter's
  `Capabilities` becomes the filtered set (no accelerator, fits the validated
  limits, narrowed by `Runtime:Docker:CapabilityAllowlist` when set); the
  status shows why each other capability is not offered (FR-024, FR-025). The
  set is refreshed with each preflight.
- **Rationale**: The platform remains the only authority for the default
  workflow and still filters offers at queue-credential issuance.
- **Alternatives considered**: Returning the capabilities with the queue
  credentials (the offer must be known before the credential request).

## R6 — Container launch settings

- **Decision**: `POST /containers/create` with `Image` = the pinned digest
  reference, `User` (R7), `Env` empty, `NetworkDisabled` and
  `HostConfig.NetworkMode = "none"`, `ReadonlyRootfs = true`, `CapDrop = ["ALL"]`,
  `SecurityOpt = ["no-new-privileges"]`, `Privileged = false`,
  `PidMode`/`IpcMode` private, `NanoCpus` and `Memory` (with `MemorySwap =
  Memory`) from the job's resources, `PidsLimit` 512, a `Tmpfs` `/tmp` of
  64 MiB, `LogConfig` `json-file` with `max-size` 10 MiB, no devices, and these
  binds: the job descriptor read-only, `AttemptLaunch.InputsDirectory` read-only
    (when staged), the result and scratch directories read-write (contract
  [analyst-container.md](contracts/analyst-container.md)). Labels
  `io.socalytics.manager`, `io.socalytics.logical-job`, and
  `io.socalytics.attempt` are set on the container. No runtime volumes are
  created: every mount is a bind of a directory under the Manager's own
  attempt root, owned by location and deleted with the attempt directory.
- **Rationale**: Implements the launch defaults of the architecture and FR-009;
  analysts need no network (staged inputs carry everything).
- **Alternatives considered**: Named volumes per attempt (adds labelled runtime
  objects to reconcile without benefit on a single host).

## R7 — Non-root user and mount permissions

- **Decision**: Containers run as `65532:65532` unless the host is Unix and the
  Manager runs as a non-root user, in which case its own uid:gid is used so
  bind-mounted directories stay writable. A Manager running as root on Unix
  refuses the profile in preflight. On Docker Desktop for Windows, bind mounts
  are writable for the container user.
- **Rationale**: Keeps the non-root rule (FR-009) and works with Linux bind
  mounts in automated verification without world-writable directories.

## R8 — Outcome mapping

- **Decision**: Exit code 0 with `manifest.json` in the result directory →
  success, then the existing `ResultPublisher` validates and stores it; a
  missing or invalid manifest → `analyst-error`; non-zero exit → `analyst-error`;
  `State.OOMKilled` → `resource-exhausted`; pull failure or a reference outside
  the registry prefix → `image-unavailable`; execution timeout →
  `execution-timeout` (existing timeout path); a lost runtime connection →
  `runtime-unavailable`. The bounded log (last 1 MiB from `/logs`) is written to
  the attempt directory for the operator and never sent to the platform.
- **Rationale**: Uses only the attempt-completion contract's sanitized
  categories (FR-012, FR-013).

## R9 — Reconciliation

- **Decision**: A `DockerReconciliationService` runs before work acquisition
  starts and then with every preflight: it lists containers filtered by
  `label=io.socalytics.manager=<registration id>`, stops and removes every one
  that is not an active attempt of the current process, removes attempt
  directories without an active attempt, and records failures for the status.
  It never lists without the label filter.
- **Rationale**: FR-016, FR-017; mirrors the architecture's labelling rule.

## R10 — Image cache

- **Decision**: `image-cache.json` in the Manager state directory records
  `{ digest, repository, sizeBytes, pulledAt, lastUsedAt }` for images the
  Manager pulled. After each attempt and with each preflight, the cache removes
  recorded images not used by an active attempt, least recently used first,
  while the recorded total exceeds `Runtime:Docker:ImageCacheBudgetBytes`
  (default 20 GiB), and every recorded image unused for longer than
  `Runtime:Docker:ImageCacheMaxIdle` (default 14 days). An image already
  present before the Manager pulled it is never recorded.
- **Rationale**: FR-026–FR-028; the signed registration state stays
  dedicated to registration.

## R11 — Test Analyst image and end-to-end verification

- **Decision**: The test Analyst lives with the end-to-end tests at
  `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/TestAnalyst/`: a
  Dockerfile on a digest-pinned `python:3-alpine` base, an `analyst.py` that
  reads `/socalytics/job.json` and writes a valid result manifest and one
  artifact to `/socalytics/result`, and the `io.socalytics.*` labels and
  `analyst-manifest.json` the Registry import expects. The test builds it with
  Testcontainers, pushes it to the existing `registry:2` fixture through the
  Docker API, imports and approves it through the Registry APIs, and runs the
  real Manager core with `Runtime:Profile=docker-desktop` against
  `unix:///var/run/docker.sock`.
- **Rationale**: Proves SC-001 against a real runtime in continuous
  integration, where Docker already runs Testcontainers; no new host tooling.
- **Alternatives considered**: A shell-only busybox Analyst (cannot build a
  valid manifest with digests reliably); a .NET Analyst image (large and slow
  to build).

## R12 — Windows verification and environment

- **Decision**: Docker Desktop on Windows is verified manually with the steps
  in [quickstart.md](quickstart.md) (§ Windows), and the evidence is recorded in
  `docs/architecture/analyst-runtime-and-recovery.md`, together with the open
  gap of automated Windows verification. No environment spec is needed: the
  verification runner already provides Docker, the .NET SDK, SoftHSM2, and
  Testcontainers; no new host tool is added.
- **Rationale**: Clarification Q1; constitution Technology and Tooling
  Constraints.

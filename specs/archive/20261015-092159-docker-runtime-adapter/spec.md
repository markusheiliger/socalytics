# Feature Specification: Docker Desktop Runtime Adapter for Analyst Execution

**Feature Branch**: `20261015-092159-docker-runtime-adapter`

**Created**: 2026-10-15

**Status**: Draft

**Input**: User description: "Docker Desktop Runtime Adapter for Analyst Execution: a registered Analyst Manager on Windows runs real Analyst containers instead of the simulated Analyst, so claimed analysis work produces real results that the platform validates and accepts as today, and the tray shows a truthful runtime preflight with remediation for every failed check. Scope: a built-in Docker Desktop adapter (Engine API, Linux-container backend, CPU only) behind the existing runtime adapter boundary; real preflight probes (local and authorized endpoint, runtime product, version, and backend against the compatibility list, registry authentication and image platform, feasible CPU and memory limits); pinned image pulls by immutable digest; constrained container launches with per-attempt scratch, result, and read-only input mounts, job resource limits, no privileges, no host namespaces, no runtime socket, and no credentials; Manager, job, and attempt labels; exit status and bounded logs mapped to the attempt outcome; stop on fencing or revocation and cleanup; startup reconciliation of AM-owned resources only; a minimal test Analyst image that proves the path end to end in continuous integration. Out of scope: accelerators, Podman, Linux, macOS, and containerd as supported profiles, signature and provenance policy, real models and capabilities, production compatibility values, packaging and updates. Depends on Analyst Manager Work Execution, Analyst Registry, Analysis Result Manifests and Validated Acceptance, and Analysis Result Read for Jobs. Implements docs/architecture/analyst-runtime-and-recovery.md: OCI Container Runtime Boundary, Built-In Runtime Adapter Profiles, Runtime Preflight And Status, and the container labelling and reconciliation rules of Registration And Runtime Credentials."

## Clarifications

### Session 2026-10-15

- Q: How should automated verification prove real container execution, given that continuous integration runs on Ubuntu and Docker Desktop exists only on Windows and macOS? → A: Continuous integration tests the adapter end to end through the local Linux Docker socket, allowed only in validation configurations; Docker Desktop on Windows is verified manually with recorded evidence. The architecture records automated verification on Windows runners with Docker Desktop as an open gap to pick up later, because it changes the repository's tooling outside the Spec Kit flow.
- Q: Which Analyst capabilities should a Manager on the Docker Desktop profile offer to the work queue? → A: By default, the Manager asks the platform for the capabilities of the stamp's default workflow and offers each one whose pinned requirements need no accelerator and fit the host's validated CPU and memory limits; the host operator may configure an optional allowlist that narrows this set.
- Q: What should happen to pulled Analyst images on the host after an attempt ends? → A: The Manager keeps the images it pulled as a cache, removes its least-recently-used images once a configurable disk budget is exceeded and those unused for a configurable period, and never removes images it did not pull.
- Q: Where should a failed Analyst container's log excerpt go? → A: Logs stay with the Manager, as the attempt-completion contract requires: the platform receives only the sanitized failure category, and the bounded container log stays on the host for the operator and is removed with the attempt's resources. Forwarding the Manager's and the Analysts' telemetry through the platform to a platform-owned OpenTelemetry sink is a separate follow-up feature. (Revised during planning: a log tail sent to the platform would have broken the contract's "logs stay with the Manager" rule.)
- Q: How does the Manager recognize the mounts it created, given that labels exist only on runtime objects? → A: The attempt mounts are host directories under the Manager's own attempt root, recognized by that location and deleted with the attempt; the Manager creates no runtime volumes, so only its containers carry the ownership labels.
- Q: Does this feature add a size limit for an Analyst's result? → A: No; result size stays governed by the result-manifest feature's existing rules, so the edge case only covers log flooding.
- Q: Where should the Analyst Manager get the credentials it uses to pull Analyst images from the OCI registry? → A: The platform issues short-lived, per-Manager, read-only pull credentials through the authenticated API, as it does for queue credentials, so pulls are authenticated and do not run into anonymous rate limits; the only registry is the stamp's configured registry and repository prefix, which in production is this repository's namespace on GitHub Container Registry (`ghcr.io`).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A Manager runs a real Analyst container for claimed work (Priority: P1)

A host operator has Docker Desktop with its Linux-container backend running on a Windows machine and an Active Analyst Manager registered with a stamp. The Manager uses the built-in Docker Desktop runtime profile instead of the simulated Analyst. When it claims a Logical Job, it pulls the job's pinned Analyst image by its immutable digest, prepares a scratch mount, a result mount, and the read-only input mount with the job's staged upstream results, and starts the Analyst container under the job's resource limits. While the container runs, the Manager keeps the attempt's lease alive. When the container exits successfully, the Manager takes the result manifest the Analyst wrote to the result mount, stores and completes the result exactly as it does today, and removes the container and its mounts.

**Why this priority**: This replaces the simulated Analyst with real execution. Every Analyst capability, high-level Analyst, and the analytics and agents built on accepted results need real containers to run on contributed hosts.

**Independent Test**: Can be fully tested by registering a Manager configured with the Docker runtime profile against a stamp in a validation environment, making a Logical Job ready whose pinned image is the minimal test Analyst, and confirming that the container ran with the pinned digest and the required restrictions, the stored result manifest was validated and accepted for exactly one attempt, and no container or mount of that attempt remains.

**Acceptance Scenarios**:

1. **Given** an Active Manager in Running with the Docker runtime profile and a claimed attempt, **When** the attempt starts, **Then** the Manager obtains the job's image by its pinned immutable digest, never by a tag, and starts exactly one Analyst container for the attempt.
2. **Given** a started Analyst container, **When** it is inspected, **Then** it runs as a non-root user with a read-only root filesystem, has only the attempt's scratch mount and result mount writable and the input mount read-only, carries the job's CPU and memory limits, has no added capabilities, no privileged mode, no host network or host process namespace, no access to the runtime endpoint, and no Manager, platform, queue, or storage credentials.
3. **Given** a started Analyst container, **When** it is inspected, **Then** it is labelled with the Manager's identity, the Logical Job, and the Execution Attempt, so the Manager can recognize it as its own.
4. **Given** the Analyst container exits with success and a result manifest in the result mount, **When** the Manager reports the outcome, **Then** it stores the result through per-object write grants and completes the attempt as the result-manifest feature specifies, and the platform accepts exactly one result.
5. **Given** the Analyst container exits with a failure, or with success but without a valid result manifest, **When** the Manager reports the outcome, **Then** it reports a failed completion for the current attempt once with the sanitized failure category that matches the exit status or the missing or invalid manifest, and keeps the bounded container log on the host for the operator until the attempt's resources are removed.
6. **Given** an attempt has ended in any way, **When** the Manager cleans up, **Then** the attempt's container, scratch mount, result mount, and input mount are removed from the host.
7. **Given** an Active Manager, **When** it needs to pull an Analyst image, **Then** it uses short-lived, read-only pull credentials it obtained through its authenticated platform API for the stamp's configured registry, and an image reference outside that registry and repository prefix fails the attempt before any pull.

---

### User Story 2 - The preflight tells the truth about the host's runtime (Priority: P2)

Before the Manager enters Running, and whenever it re-checks, it runs the runtime preflight against the configured Docker Desktop endpoint. The tray window and the CLI show each check with its result and, for every failed check, remediation guidance. The Manager offers capabilities and pulls work only while every required check passes; otherwise it stays registered in Runtime unavailable.

**Why this priority**: Host operators install and update Docker themselves. Without a truthful preflight, a Manager would claim work it cannot run and burn attempt budget. This story makes Story 1 safe on real hosts.

**Independent Test**: Can be fully tested by running the preflight against a reachable supported runtime and observing Running with the offered capabilities, then against an unreachable endpoint, a remote endpoint, an unsupported runtime version or backend, failing registry authentication, an image for another platform, and infeasible resource limits, and observing Runtime unavailable with the failed check and its remediation in the tray window and the CLI.

**Acceptance Scenarios**:

1. **Given** a local, reachable, and authorized runtime endpoint whose product, version, backend, operating system, and architecture match an entry of the Manager's compatibility list, **When** the preflight runs, **Then** every check passes, the Manager enters Running, and it offers each capability of the stamp's default workflow that needs no accelerator and fits its validated limits, narrowed by the operator's allowlist when one is configured.
2. **Given** a runtime endpoint that is not reachable, **When** the preflight runs, **Then** the Manager stays in Runtime unavailable, the endpoint check fails with remediation that tells the operator to start Docker Desktop, and the Manager pulls no work.
3. **Given** a configured runtime endpoint that is not local (for example a remote network address), **When** the preflight runs, **Then** the endpoint check fails, the Manager never connects to that endpoint, and it stays in Runtime unavailable.
4. **Given** a runtime whose version, backend, or operating system matches no compatibility entry, **When** the preflight runs, **Then** the profile check fails with the detected values and the supported ones, and the Manager stays in Runtime unavailable instead of using an unvalidated profile.
5. **Given** the registry rejects the Manager's image access, or the probe image's platform does not match the host's runtime, **When** the preflight runs, **Then** the registry or image-platform check fails with remediation and the Manager stays in Runtime unavailable.
6. **Given** configured CPU, memory, or concurrency limits the runtime cannot provide, **When** the preflight runs, **Then** the resource check fails with the available and requested values, and the Manager stays in Runtime unavailable.
7. **Given** a Running Manager whose runtime becomes unavailable (Docker Desktop stops), **When** the next preflight runs, **Then** the Manager enters Runtime unavailable, stops pulling new work, and handles its active attempts as the work-execution feature specifies for a lost runtime.

---

### User Story 3 - Crashes and revocation leave no orphaned containers (Priority: P3)

Hosts crash, the Manager is killed, attempts are fenced, and Club Admins revoke Managers. In every case the Manager stops the affected Analyst containers and removes them, and when it starts again it inspects only the containers labelled as its own and the attempt directories under its own attempt root, keeps nothing that belongs to a fenced or finished attempt, and never touches the operator's other containers.

**Why this priority**: Contributed hosts must stay clean and trustworthy. This story depends on Story 1 for the containers it cleans up, and extends the existing recovery rules to real containers.

**Independent Test**: Can be fully tested by stopping the Manager while an Analyst container runs, starting it again, and observing that the leftover container and mounts are removed while an unrelated, unlabelled container on the same runtime stays untouched; and by fencing or revoking an attempt while its container runs and observing that the container is stopped and removed.

**Acceptance Scenarios**:

1. **Given** a running Analyst container, **When** its attempt is fenced (stale lease, refused heartbeat or completion), **Then** the Manager stops and removes the container and its mounts and sends no further heartbeat or completion for that attempt.
2. **Given** a running Analyst container, **When** the job's execution timeout elapses, **Then** the Manager stops the container, removes it, and reports a failed completion that names the timeout.
3. **Given** a Manager with running Analyst containers, **When** a Club Admin revokes it and the Manager observes the revocation, **Then** it stops and removes all its Analyst containers and mounts.
4. **Given** the Manager was stopped or crashed while Analyst containers ran, **When** it starts again, **Then** it finds the containers labelled with its own identity and the attempt directories under its own attempt root, stops and removes them, and does not resume, heartbeat, or complete their attempts, which recover through lease expiry.
5. **Given** containers on the same runtime that are not labelled with this Manager's identity, including containers of another Manager on the same host, **When** the Manager reconciles or cleans up, **Then** it neither inspects their contents nor stops or removes them.

---

### Edge Cases

- The pinned image cannot be pulled (registry unavailable, digest not found, platform mismatch): the attempt fails once with a named reason before any container starts, and nothing is left on the host.
- The Manager cannot obtain or renew its pull credentials (platform unavailable, Manager revoked): it does not claim new work that needs a pull; a revoked Manager stops as the work-execution feature specifies, and an attempt whose pull fails for lack of credentials fails once with a named reason.
- The image pull takes longer than one heartbeat interval: the Manager keeps the lease alive during the pull, because the attempt is already claimed.
- An Analyst floods its log: the Manager keeps only a bounded log on the host; the result itself is subject to the result-manifest feature's existing rules.
- The Analyst container is killed by the runtime for exceeding its memory limit: the Manager reports a failed completion that names the out-of-memory exit.
- The runtime stops while a container runs: the Manager reports a failed completion for the attempt if it can still reach the platform; otherwise the attempt recovers through lease expiry; it enters Runtime unavailable either way.
- A container labelled with this Manager's identity belongs to an attempt the platform still considers active after a quick restart: the Manager still removes it and does not adopt it, because a new process never resumes attempts of its previous process.
- A container removal fails (the runtime refuses or times out): the Manager retries on its next reconciliation and shows the leftover resource in the tray window; it never removes unlabelled resources to make room.
- The image cache exceeds its disk budget while every cached image is used by an active attempt: the Manager removes nothing in use, shows the budget overrun, and removes images as soon as their attempts end.
- An image the Manager pulled was also pulled or used by the host operator: the Manager may remove it under its cache rules only when no container uses it; the operator's own containers are never removed.
- The host operator selects the Docker profile on an operating system other than Windows: the profile check fails and the Manager stays in Runtime unavailable, except in validation environments that explicitly allow the test endpoint (see Assumptions).
- An image declares a root user or requests privileged settings: the Manager's restrictions override the image's defaults; an Analyst that cannot run under them fails its attempt with a named reason.
- The stamp's default workflow changes while the Manager runs: the Manager refreshes its offered capabilities with its next preflight or status check; active attempts are unaffected, and the platform keeps assigning only exact id and version matches.
- No capability of the default workflow fits the host (all need an accelerator or exceed its limits, or the allowlist excludes all): the Manager stays Running with an empty offer, pulls no work, and shows why each capability is not offered.

## Requirements *(mandatory)*

### Functional Requirements

#### Runtime profile and endpoint

- **FR-001**: The Analyst Manager MUST ship a built-in Docker Desktop runtime profile behind its runtime-neutral adapter boundary, alongside the simulated runtime used for development and test, without any discovery or loading of external adapters.
- **FR-002**: The Docker Desktop profile MUST connect only to a local runtime endpoint (the configured or default local named pipe) and MUST refuse remote or unauthenticated network endpoints.
- **FR-003**: Only the Manager process MUST use the runtime endpoint; Analyst containers MUST never receive access to it.

#### Preflight

- **FR-004**: The preflight MUST probe and report the runtime product, version, backend, operating system and version, and architecture, and MUST decide support only by matching them against the Manager's compatibility list, never from the product name alone.
- **FR-005**: The preflight MUST check that the endpoint is reachable, local, and authorized; that the Linux-container backend is available; that registry authentication succeeds and the configured probe image's platform matches the runtime; and that the configured CPU, memory, and concurrency limits are feasible on the runtime.
- **FR-006**: Every failed preflight check MUST be shown in the tray window and the CLI with its detected values and remediation guidance, and the Manager MUST stay registered in Runtime unavailable, offering no capability and pulling no work, until every required check passes.
- **FR-007**: The Docker Desktop profile MUST advertise only CPU execution; it MUST NOT advertise any accelerator.
- **FR-024**: Once its preflight passes, the Manager MUST obtain from the platform, through its authenticated API, the capability ids, versions, and pinned resource requirements of the stamp's default workflow, and MUST offer exactly those capabilities that need no accelerator and fit its validated CPU and memory limits, narrowed by the host operator's optional capability allowlist when one is configured.
- **FR-025**: The tray window and the CLI MUST show the offered capabilities and, for each capability of the default workflow that is not offered, the reason (accelerator required, resource requirements exceed the host's limits, or excluded by the allowlist).

#### Registry access

- **FR-021**: The platform MUST issue an Active Manager short-lived, read-only image pull credentials of its own through its authenticated platform API, limited to the stamp's configured registry host and repository prefix, and MUST issue none to a revoked or inactive Manager.
- **FR-022**: The Manager MUST pull images only with those credentials, renew them before they expire, keep them only in memory, never pass them to an Analyst container, and never store a durable registry secret.
- **FR-023**: The Manager MUST refuse an image whose reference lies outside the stamp's configured registry host and repository prefix, failing the attempt with a named reason before any pull.

#### Attempt execution

- **FR-008**: For each claimed attempt, the Manager MUST obtain the Analyst image only by the job's pinned immutable digest and MUST fail the attempt with a named reason when the digest cannot be obtained or does not match the runtime's platform.
- **FR-009**: The Manager MUST start exactly one Analyst container per attempt, with a non-root user, a read-only root filesystem, writable scratch and result mounts scoped to that attempt, a read-only input mount holding the attempt's staged upstream results, the job's CPU and memory limits, all capabilities dropped, no privileged mode, no host network or host process namespace, no runtime endpoint, and no Manager, platform, queue, or storage credentials.
- **FR-010**: The Manager MUST label every container it creates with its Manager identity, the Logical Job, and the Execution Attempt, and MUST create every attempt mount as a directory under its own attempt root, without creating runtime volumes.
- **FR-011**: The Manager MUST keep heartbeating a claimed attempt from claim through image pull, container run, and result storage, as the work-execution feature specifies.
- **FR-012**: When the container exits, the Manager MUST map its exit status and the result mount's contents to the attempt outcome: success only with a zero exit status and a result manifest in the result mount; otherwise a failure whose sanitized category matches the cause (non-zero exit, out-of-memory kill, timeout, unavailable image, or missing or invalid manifest), with the detailed cause shown to the operator on the host.
- **FR-013**: The Manager MUST keep a bounded log of each container on the host, shown to the operator and removed with the attempt's resources, and MUST send the platform only the sanitized failure category of the attempt-completion contract, never log content.
- **FR-014**: The Manager MUST stop and remove the container when the job's execution timeout elapses, when the attempt is fenced or refused, when the Manager is revoked, and when a drain policy stops remaining work.
- **FR-015**: After every attempt, the Manager MUST remove the attempt's container and mounts, and MUST retry removals that fail on its next reconciliation.

#### Image cache

- **FR-026**: The Manager MUST keep the images it pulled as a local cache and reuse a cached image when a later attempt pins the same digest.
- **FR-027**: The Manager MUST record which image digests it pulled and when each was last used, remove its least-recently-used images not used by an active attempt once its configurable disk budget is exceeded, and remove its images not used for a configurable period.
- **FR-028**: The Manager MUST NOT remove an image it did not pull, even when that image is unused or the disk budget is exceeded.

#### Reconciliation

- **FR-016**: When it starts and periodically while it runs, the Manager MUST find the containers labelled with its own Manager identity and the attempt directories under its own attempt root, stop and remove those that belong to no attempt of its current process, and never resume, heartbeat, or complete attempts of a previous process.
- **FR-017**: The Manager MUST NOT inspect the contents of, stop, or remove containers that are not labelled with its own Manager identity, or files outside its own attempt root.
- **FR-018**: The tray window and the CLI MUST show the runtime profile and version, endpoint and backend health, the disk use of the Manager's image cache against its budget, and the reconciliation state, including resources the Manager could not remove.

#### Verification

- **FR-019**: The repository MUST provide a minimal test Analyst image that reads its input mount and writes a valid result manifest to its result mount, built by the automated tests, so the end-to-end path from claim to accepted result runs against a real container runtime in the verification environment through the local Linux runtime socket, which the Manager accepts only in validation configurations.
- **FR-020**: The architecture MUST record that Docker Desktop on Windows is verified manually in this feature and that automated verification on Windows runners is an open gap.

### Key Entities

- **Runtime Profile**: The built-in Docker Desktop profile: its endpoint, the detected runtime facts, the compatibility entry it matched, and its preflight checks with results and remediation.
- **Analyst Container**: The single container that runs one Execution Attempt from the pinned image digest, with its restrictions, resource limits, mounts, and identity labels.
- **Attempt Mounts**: The job file and the scratch, result, and read-only input directories of one attempt, under the Manager's own attempt root and removed with the attempt.
- **Ownership Labels**: The Manager, Logical Job, and Execution Attempt identities on every container the Manager creates, used to reconcile only its own containers.
- **Registry Pull Credential**: A short-lived, read-only credential the platform issues to one Active Manager for the stamp's configured registry host and repository prefix; held only in the Manager's memory.
- **Image Cache Entry**: An image digest the Manager pulled, with its size and the time it was last used, kept until the disk budget or the unused period removes it.
- **Test Analyst Image**: A minimal Analyst image used only in automated verification that produces a valid result manifest.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In the verification environment, a ready Logical Job whose pinned image is the test Analyst is claimed, executed in a real container, and its result accepted, with exactly one accepted result and no leftover container or mount, in 100% of runs.
- **SC-002**: Every launched Analyst container satisfies all isolation restrictions of FR-009 when inspected, in 100% of verified attempts.
- **SC-003**: Every failure mode named in the acceptance scenarios and edge cases (pull failure, non-zero exit, out-of-memory kill, timeout, missing or invalid manifest, fencing, revocation) ends with exactly one failed completion or a recovery through lease expiry, and no orphaned container, in 100% of test runs.
- **SC-004**: After a Manager restart that follows a crash during execution, all of its labelled leftover resources are removed before it pulls new work, and no unlabelled container on the same runtime is changed.
- **SC-005**: For every failed preflight check, the host operator sees the failed check, the detected values, and a remediation step in the tray window and the CLI, and the Manager pulls no work while any required check fails.

## Assumptions

- **Dependencies**: Analyst Manager Work Execution (claims, heartbeats, completion, fencing, revocation, drain), Analyst Registry (pinned image digests and resource profiles), Analysis Result Manifests and Validated Acceptance (result mount, write grants, manifest validation), and Analysis Result Read for Jobs (staging accepted upstream results into the attempt's input mount).
- **Supported profile**: Docker Desktop for Windows with the Linux-container backend on CPU is the only supported profile of this feature. The compatibility list may contain only entries this feature verifies; production compatibility values remain unresolved.
- **Verification environment**: The automated verification runs on Linux. It exercises the same runtime API end to end through the local Linux Docker socket, an endpoint allowed only in validation configurations; the Windows named pipe and Docker Desktop itself are verified manually and that evidence is recorded, without claiming production readiness. Automated verification on Windows runners with Docker Desktop is not part of this feature: it changes the repository's tooling outside the Spec Kit flow, so the architecture records it as an open gap to pick up later.
- **Capabilities**: The platform remains the authority for which capability a Manager is assigned: it assigns only exact id and version matches with the resolved default workflow. Real Analyst capabilities and models are delivered by later features; in verification, the default workflow resolves to the test Analyst image.
- **Registry access**: The only image registry is the stamp's configured registry host and repository prefix, which the Analyst Registry already uses for imports: this repository's namespace on GitHub Container Registry (`ghcr.io`) in production and a disposable registry in tests. The platform derives the Managers' short-lived pull credentials from its protected registry read access; their lifetime is a configurable value, since production credential lifetimes remain unresolved under `GOV-CRED-004`. Image signature and provenance verification are delivered by a later feature.
- **Out of scope**: CUDA and other accelerators, Podman, Linux, macOS, and containerd as supported profiles, model digests beyond passing them through, Manager packaging and updates, forwarding Manager and Analyst telemetry to a platform OpenTelemetry sink (a follow-up feature), and installing, updating, or configuring Docker Desktop, which stays the host operator's responsibility.
- **Architecture**: This feature implements the sections OCI Container Runtime Boundary, Built-In Runtime Adapter Profiles, Runtime Preflight And Status, and the container labelling and reconciliation rules of Registration And Runtime Credentials in `docs/architecture/analyst-runtime-and-recovery.md`, and adds there the verification status of the Docker Desktop profile with the open gap of automated Windows verification, and the platform-issued registry pull credentials next to the API and queue credentials in Registration And Runtime Credentials.

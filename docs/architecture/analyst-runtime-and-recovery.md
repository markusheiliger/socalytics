# Analyst Runtime and Recovery (Provisional)

This document defines the external OCI runtime boundary and durable execution
attempt behavior. The installed host lifecycle is defined by the
[Analyst Manager architecture](analyst-manager.md).

## OCI Container Runtime Boundary

The Analyst Manager application remains cross-platform, but the first supported
host for Analyst-container execution is Windows. Linux and macOS execution
profiles are future provisional work. OCI images remain the portable packaging
contract; the container engine is an external host dependency rather than part
of the AM installation.

The AM operates containers through an internal, transport-neutral OCI Runtime
Adapter. The adapter must:

- probe runtime identity, version, endpoint health, operating system,
  architecture, backend, and device capabilities
- authenticate to the OCI registry and pull an image by immutable digest
- create, start, inspect, stop, and remove an Analyst container
- apply constrained mounts, environment, devices, network, and resource limits
- collect exit status and bounded logs
- label, reconcile, and clean up AM-owned resources left after a crash

Hardware-neutral jobs and platform APIs contain no Docker- or Podman-specific
fields. The AM advertises only execution capabilities that the active runtime
profile has validated on the host, not capabilities inferred from the runtime
product name.

Runtime endpoints are configured or discovered by a supported profile. A
Docker Desktop profile on Windows may use
`npipe://./pipe/docker_engine`; another profile may use a different named pipe,
Unix socket, or authenticated local endpoint. Local access is the default and
unauthenticated remote TCP runtime APIs are unsupported.

### Built-In Runtime Adapter Profiles

- **Docker Desktop for Windows:** the first shipped adapter and profile, using its
  Engine API and Linux-container backend. CPU is the mandatory baseline and
  CUDA through WSL2 is the first accelerated path to test.
- **Podman for Windows:** a future separate built-in adapter assembly to ship
  only after validation through its compatible API and Podman Machine. Podman
  is not bundled with the AM.
- **Future profiles:** Linux, macOS, and containerd support remain outside the
  first-release execution scope. The Linux profiles will prefer rootless Podman
  or Docker and also accept a rootful Docker daemon reached through the
  `docker` group; preflight reports which one is in use, and the rootful case
  requires a dedicated non-login service account for the headless server
  profile (see [Analyst Manager](analyst-manager.md#headless-server-profile));
  under a login-capable account the headless Manager reports the runtime as
  unavailable and admits no work. The rule is implemented as a failing
  preflight check and exercised with the simulated runtime and, for the root
  check, the Docker Desktop adapter; the Linux runtime adapters that report the
  daemon access remain future.

Support is defined by a tested compatibility matrix containing the AM version,
runtime product and version, backend or VM mode, Windows version, architecture,
and accelerator configuration. Detecting a runtime does not by itself make the
profile supported.

The Docker Desktop profile (`Runtime:Profile` `docker-desktop`, configured
through `Runtime:Docker`) is implemented and verified end to end: automated
verification runs the same Engine API adapter against a Linux Docker engine
through its local Unix socket, an endpoint the AM accepts only in Development
and Test configurations. Docker Desktop on Windows itself is verified manually
and awaits that manual evidence; only manually verified versions enter the
shipped compatibility matrix, so no compatibility entry ships yet. Automated
verification on Windows runners with Docker Desktop is an open gap: it changes
the repository's build and verification tooling and is outside the Spec Kit
implementation flow.

Adapter assemblies are versioned and shipped with the Analyst Manager. They
share one runtime-neutral interface; installable plugins and arbitrary external
adapter loading are unsupported.

Users or IT install, configure, license, update, and start the runtime and its
VM or backend. No Analyst Manager installer or update installs, upgrades,
removes, or reconfigures Docker, Podman, or their virtual
machines. The AM validates compatibility and
uses least-privilege local access. An upgrade outside the compatibility matrix
moves the AM to `Runtime unavailable` until the profile is validated again.

### Runtime Preflight And Status

After stamp registration and before entering `Running`, the AM verifies:

- the runtime endpoint is reachable, local, and authorized
- the runtime and profile versions are supported
- the Linux-container backend or VM is available
- OCI registry authentication and image-platform compatibility
- configured CPU, memory, disk, and concurrency limits are feasible
- every advertised accelerator passes a functional device probe

A failed preflight leaves the AM registered in `Runtime unavailable`. It stays
connected for status and revocation but cannot advertise the failed capability
or dequeue work. The tray window shows the failed check and remediation guidance
without silently selecting an unvalidated profile. It also shows the active
profile and version, endpoint and backend health, runtime disk use, validated
accelerators, and resource-reconciliation state.

The runtime endpoint is a privileged host-control boundary. Only the AM process
account receives access. Analyst containers never receive that endpoint, AM
credentials, or host-wide control APIs. Analyst launches default to:

- immutable image digests with the configured signature and provenance checks
- a non-root container user and read-only root filesystem where supported
- explicit writable scratch and result mounts scoped to one job, and a
  read-only input mount into which the AM stages the job's accepted upstream
  results before launch
- no object-storage write credentials: results go to the job's result mount,
  and the AM stores them through per-object write grants; short-lived read
  access only where a capability permits it
- no privileged mode, host PID namespace, host network, or runtime socket mount
- allowlisted devices, dropped capabilities, CPU and memory limits, and bounded
  logs

The Analyst container interface fixes what an Analyst sees: the Logical Job message read-only at `/socalytics/job.json`, the
read-only attempt context at `/socalytics/attempt.json`, the
staged upstream results read-only at `/socalytics/inputs/`, the result mount
at `/socalytics/result/` (where the Analyst writes its result manifest and
artifacts), scratch space at `/socalytics/scratch/`, and a small memory-backed
`/tmp`. Networking is disabled, and the AM sets no environment variables, so no
credential, grant, or platform address reaches the container. Every container
carries the AM's Manager, Logical Job, and Execution Attempt labels. The
container's output stays on the host as a bounded log for the operator; the
platform receives only the sanitized failure category of the attempt
completion.

The interface is completed as follows (see
[Analysts, Models, and Hardware](analysts-models-and-hardware.md#implementation-profile)
for the Analyst SDK that consumes it): `/socalytics/job.json` holds the
received Logical Job message byte for byte, and job messages of contract
version 1.1.0 state the run's workflow and, for segment-scoped jobs, the
segment identity and the half-open nominal window that result acceptance
compares. The AM also writes a read-only attempt context to
`/socalytics/attempt.json` (`contracts/analysis/analyst-attempt/v1`) with the
claimed attempt's identity and number, and nothing else: no lease, fencing
token, grant, or address.

Segment media reaches an Analyst only through the AM: for a job whose message pins segment media, the AM obtains, for
its current, unfenced attempt, the segment manifest and short-lived per-object
read grants for the manifest and the media (`getAnalysisSegmentMedia`,
response contract `contracts/analysis/segment-media/v1/`), verifies the
manifest against the job's pinned checksum and the media against the
manifest's digest and size within the attempt's staging bound, and stages both
read-only at `/socalytics/media/` (`segment-manifest.json` and `segment.mp4`).
The mount is absent for every other job, and the staged media is deleted with
the attempt directory.

### Registration And Runtime Credentials

The durable AM registration is not a durable bearer secret. The AM proves its
protected device key to obtain short-lived, DPoP-bound API tokens. Through that
authenticated API session it obtains expiring NATS credentials restricted to
the registered stamp and the subjects required to pull and acknowledge work.
API and NATS connections use authenticated TLS. It never stores a
registration-lifetime broker credential.

Through the same session an Active AM obtains short-lived, read-only OCI
registry pull credentials. The platform derives
them from its protected registry read access through the registry's token
service, limited to the repositories of the default workflow's images inside
the stamp's configured registry host and repository prefix (this repository's
namespace on GitHub Container Registry in production). The AM keeps them in
memory only, renews them before they expire, passes them only to the local
runtime for pulls, and refuses images outside that prefix. Revoked or inactive
Managers obtain none.

The Manager offers the capabilities its validated runtime provides, each as a
capability id and a semantic version. On the Docker Desktop profile it obtains
the capability ids, versions, and pinned resource requirements of the default
workflow through its authenticated API and offers those that need no
unvalidated accelerator and fit its validated CPU and memory limits, narrowed
by an optional operator allowlist. The platform
provisions one work consumer
per capability of the default workflow definition resolved from the Analyst
Registry, and assigns a Manager the consumer of an offered capability only when
the offered id and semantic version equal that resolved definition's; other
offered capabilities are ignored. Credential lifetimes and production values
remain unresolved under `GOV-CRED-004`.

Administrative revocation immediately blocks API authorization and credential
renewal, revokes the current NATS identity where supported, and fences every
active attempt owned by the Manager. The platform rejects later heartbeat and
completion calls and reschedules work through normal lease recovery. It also
instructs the connected AM to stop and remove its active containers, but
correctness and access control do not depend on delivery of that instruction.

Outstanding object-storage grants are invalidated when the storage mechanism
supports revocation. Unavoidable presigned grants use short lifetimes and narrow
job scope; attempt fencing prevents their results from becoming accepted after
Manager revocation.

Runtime secrets, logs, grants, cached data, images, and result artifacts follow
the classification, vulnerability, retention, and deletion requirements in
[Security and Data Governance](security-and-data-governance.md).

The AM labels every managed container and volume with its Manager, job,
and execution-attempt identities. Startup reconciliation inspects only those
AM-owned resources. It adopts or terminates them according to the durable lease
and fencing state and removes stale resources without touching unrelated user
containers.

---

## Execution Attempts, Heartbeats, And Recovery

Low-level and high-level Analyst Containers use this same runtime path. Both
tiers use identical claim, heartbeat, lease, fencing, retry, idempotent
completion, OCI isolation, and cleanup semantics; match-scoped high-level work
does not create a separate execution system.

The logical job identity remains stable across retries. Each dequeue creates a
distinct execution attempt in the durable Job Registry. The platform returns
an opaque lease/fencing token for that attempt, and the AM acknowledges the
queue delivery only after the claim succeeds. A new attempt and token are
issued whenever a re-enqueued logical job is claimed.

While an Analyst container is active, the AM periodically sends a heartbeat to
the stamp API containing the Manager, logical job, execution attempt,
lease token, timestamp, and execution state. Progress and lightweight resource
measurements may be included when available. The API validates the registered
Manager, current attempt, and token before renewing the lease.

Heartbeat interval and stale timeout are platform-configurable. The timeout
must safely exceed the interval and tolerate transient network delay. A
control-plane Job Monitor uses the durable Job Registry to detect expiry. The AM
never decides that its own attempt should be re-enqueued.

Production profiles assign and approve heartbeat, stale-timeout, retry,
runtime-compatibility, reconciliation, and alert bounds under
[Production Deployment and Operations](production-operations.md). This topic
does not supply unevidenced numeric defaults; missing production values block
readiness while preserving the current lease and fencing contract.

On expiry, the platform atomically marks the attempt stale, invalidates its
token, and republishes the logical job so another Manager can claim a new
attempt only when the immutable logical-job attempt budget remains. The stale
attempt consumes one entry in that budget. On exhaustion, the logical job fails
permanently and is not republished. A recovered old AM cannot renew or complete
the stale attempt. Late heartbeats and callbacks are rejected or acknowledged
as obsolete without changing the accepted result.

Cancelling an analysis run (implemented; see
[Run State And Recovery](job-processing.md#run-state-and-recovery)) closes
its active attempts in the cancellation's transaction, like expiry invalidates
a token, but neither republishes their logical jobs nor consumes attempt
budget. The AM learns of it when its next heartbeat, result-grant request,
upstream read, or completion is refused as obsolete, and then stops the
Analyst container and removes the attempt's staged inputs and result data as
best effort.

```mermaid
sequenceDiagram
    participant Jobs as NATS Analysis Jobs
    participant Manager as Analyst Manager
    participant API as SocAlytics API
    participant Registry as Job Registry
    participant Monitor as Job Monitor
    participant Adapter as OCI Runtime Adapter
    participant Runtime as External OCI Runtime
    participant Analyst as Analyst Container
    participant Storage as Object Storage

    Manager->>Jobs: Pull compatible stamp-local job
    Jobs-->>Manager: Logical job
    Manager->>API: Claim job for this Manager
    API->>Registry: Create attempt and lease
    Registry-->>API: Attempt ID and fencing token
    API-->>Manager: Attempt ID and fencing token
    Manager->>Jobs: Acknowledge delivery
    Manager->>Adapter: Launch constrained attempt
    Adapter->>Runtime: Create and start container
    Runtime->>Analyst: Run Analyst
    loop While Analyst is active
        Manager->>API: Heartbeat with attempt and token
        API->>Registry: Validate and renew lease
    end
    alt Attempt completes before expiry
        Analyst-->>Manager: Result manifest and outcome in the result mount
        Manager->>API: Request per-object result write grants
        Manager->>Storage: Store result objects with the grants
        Manager->>API: Idempotent completion with token
        API->>Registry: Accept current attempt once
    else Heartbeat lease expires
        Monitor->>Registry: Mark attempt stale and fence token
        Monitor->>Jobs: Publish logical job for a new attempt
        Manager-->>API: Late heartbeat or completion
        API-->>Manager: Attempt is obsolete
    end
```

Execution is therefore at least once: duplicate physical work can occur around
host failures or network partitions. Result paths, manifests, callbacks, and
job-state transitions must be idempotent and fenced by execution attempt. Only
one attempt can become the accepted completion for a logical job.

Each capability declaration supplies its resource, retry, and timeout profile.
Analysis-run creation resolves those requirements through the approved Analyst
profile and copies `max_attempts`, lease, stale-timeout, execution-timeout, and
resource values into each immutable logical-job snapshot. The Analyst Manager
admits a job only when the validated host can satisfy its pinned requirements.
Match-level high-level jobs may declare longer leases, timeouts, or larger
resource bounds than segment jobs, but heartbeat and fencing behavior remains
unchanged.

Model-backed jobs identify immutable Analyst-profile, OCI image, and model
digests. Full-frame, tiled, hybrid, and other preprocessing strategies remain
private to that image; they may influence the profile's declared resource and
runtime requirements but are not Manager-selected job parameters. A retry,
including one claimed by another Manager, must execute the same image and model
pair. Retry and fencing semantics remain identical across implementations.

---

## Analyst Manager Responsibilities

- connect and register with one deployment stamp
- bind the registration to exactly one deployment stamp
- protect the registered asymmetric device key in an operating-system keystore
- obtain and rotate short-lived API and NATS credentials, and obtain
  short-lived read-only registry pull credentials that stay in memory
- restore the stamp-scoped identity and last pause state on autostart
- advertise hardware and runtime capabilities
- validate the external runtime profile before advertising capabilities
- enforce the effective concurrency limit and pause state before pulling work
- pull compatible stamp-local analysis jobs from NATS and claim execution attempts
- read and stage accepted upstream results (manifests, artifacts, and coverage,
  digest-verified and size-bounded) into the attempt's read-only input mount
  before launching high-level attempts, and delete them when the attempt ends
- read and stage a segment job's pinned segment manifest and media,
  digest-verified and size-bounded, into the attempt's read-only media mount
  before launch, and delete them when the attempt ends
- pull the image digest that the job pins (the platform assigns it; the AM
  never selects an image) and refuse images outside the stamp's registry prefix
- launch and constrain the selected Analyst container through the runtime
  adapter
- monitor execution and renew each active attempt lease through heartbeats
- check the result manifest against its contract, store the result objects
  through per-object write grants, and report the result manifest, checksum,
  runtime, image digest, and fencing token
- expose local utilization, active attempts, and timestamped queue status
  (implemented for CPU, memory, and queue depth; accelerator utilization
  follows an accelerated runtime)
- support pause, resume, drain, local unregister, and platform revocation
- terminate and clean up active containers after security revocation
- reconcile AM-owned runtime resources and destroy containers on completion
- keep the images it pulled as a cache within a disk budget and an unused
  period, and never remove images it did not pull

The Analyst Manager understands the participating host's hardware.

The platform does not.

---

Related architecture: [Index](README.md) |
[Analyst Manager](analyst-manager.md) | [Job Processing](job-processing.md) |
[Analysts, Models, and Hardware](analysts-models-and-hardware.md) |
[Production Deployment and Operations](production-operations.md)

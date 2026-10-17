# Research: Analyst Manager Queue Depth and Host Utilization

## R1. Where the count comes from

- **Decision**: The platform reads JetStream consumer info for `analyst-<capability-id>` on the jobs stream and returns its `NumPending` (messages matching the consumer's filter that were not yet delivered) as the waiting count. The observation time is the platform's time when it read the consumer info.
- **Rationale**: `NumPending` is exactly "ready jobs not yet delivered to any Manager" (spec Assumptions → Counting); delivered-but-unacknowledged messages (`NumAckPending`) are excluded. The consumer already exists per capability (`NatsTopology`), so no new broker state is needed.
- **Alternatives considered**: Counting ready Logical Jobs in PostgreSQL (rejected: published jobs and queue state can differ, and the architecture names the queue as the source); stream message counts per subject (rejected: they include delivered messages).

## R2. Which capabilities a Manager asks for

- **Decision**: `GET /api/v1/analyst-managers/self/queue-depth?capability=<id>&capability=<id>…` with the capability ids of the Manager's current consumer assignments (`QueueCredentialProvider.Current`). The platform answers only for ids whose consumer the resolved default workflow defines (the same set queue-credential issuance can assign); other ids are omitted, at most 64 ids per request, each `[a-z0-9][a-z0-9.-]{0,63}`. Active state is checked like the other self operations (401 for inactive registrations from the DPoP handler; 403 `manager-not-active` otherwise).
- **Rationale**: Assignments are computed per credential request and not persisted on the platform (`IssueQueueCredentials.cs`), so the Manager knows its assignment and the platform re-checks it against the same source. A stamp hosts exactly one club, so counts for capabilities of the club's own default workflow reveal nothing beyond the club.
- **Alternatives considered**: Persisting assignments at issuance (rejected: a new table for display guidance); returning every capability of the workflow and filtering in the Manager (rejected: FR-002 limits the response to the caller's capabilities).

## R3. Failure mode

- **Decision**: When the platform cannot read consumer info (broker unreachable or timeout of 2 seconds per capability), it returns `503` with problem type `urn:socalytics:problem:queue-unavailable`; a missing consumer counts as an omitted capability. No audit event (read-only guidance).
- **Rationale**: FR-004 distinguishes unavailability from authorization failures; the Manager marks its last observation stale.

## R4. Polling in the Manager

- **Decision**: `RegistrationStatusMonitor` requests queue depth after each successful registration check while the registration is active and a queue credential with at least one assignment exists; a `QueueDepthState` singleton keeps the latest observation in memory (platform `observedAt`, local `receivedAt`, per-capability counts, total, last error). It is stale when `now − receivedAt > 2 × interval` or the latest request failed. Age is computed from `receivedAt` on the Manager's clock, so clock skew between Manager and platform does not matter. Without assignments the state is `no-capability`; before the first response it is `not-yet-available`. On revocation or unregistration the state is cleared.
- **Rationale**: One loop, one interval (the platform-provided status check interval); no persistence (FR-006).
- **Alternatives considered**: A separate background service with its own interval (rejected: a second timer and a second token use for the same cadence).

## R5. Host utilization sampling

- **Decision**: `HostUtilizationSampler` (a background service in Core) samples every 5 seconds and keeps the latest sample: CPU utilization as busy share of total CPU time between two readings, memory in use and total. Linux: `/proc/stat` and `/proc/meminfo` (`MemTotal − MemAvailable`). Windows: `GetSystemTimes` and `GlobalMemoryStatusEx` (P/Invoke). macOS: `host_statistics(HOST_CPU_LOAD_INFO)`, `host_statistics64(HOST_VM_INFO64)`, and `sysctl hw.memsize` (P/Invoke to `libSystem`). A value that cannot be read is `null` ("not available"). Parsing and arithmetic are pure functions tested on every platform; the OS calls run in tests on Linux CI and are verified on Windows and macOS like the key stores.
- **Rationale**: No package needed; these are the documented per-OS sources; the first sample after start has no CPU value until the second reading.
- **Alternatives considered**: `System.Diagnostics.PerformanceCounter` (Windows only); process-level counters (rejected: the operator needs the host's load).

## R6. Display

- **Decision**: `ManagerStatusSnapshot` gains `QueueDepth` and `HostUtilization`; the local-control `status` response and `src/analysts/manager/contracts/local-control.schema.json` gain `queueDepth` (`state`: `available` | `stale` | `not-yet-available` | `no-capability` | `unavailable`, `observedAt`, `ageSeconds`, `capabilities` [{`id`, `waiting`}], `total`) and `hostUtilization` (`sampledAt`, `cpuPercent`, `memoryUsedBytes`, `memoryTotalBytes`, nulls for not available). The CLI text output adds "Queue (approximate guidance)" lines and "CPU" and "Memory" lines; the Avalonia status window adds a "Waiting work (approximate)" section and a "Host" section. The status report built by `StatusReportBuilder` does not change, and a test asserts it carries neither value (FR-013).
- **Rationale**: One snapshot feeds the window, the tray, and the CLI.

## R7. Contracts and tooling

- **Decision**: Add `contracts/analyst-manager/queue-depth/v1/queue-depth.schema.json` with examples (available, empty, one capability omitted), validated by a new Contracts test and a Manager golden fixture test, following `queue-credentials/v1`. Update the OpenAPI snapshot. No new packages; .NET 10, Docker (NATS and PostgreSQL containers), and the Manager test tooling are provided by the environment actions, so no environment feature is needed.
- **Rationale**: The constitution makes machine-readable contracts authoritative and shared fixtures keep the two implementations aligned.

## R8. Interplay with the headless server profile

- **Decision**: This feature changes the CLI status, the local-control schema, and `ManagerStatusSnapshot`, which the Analyst Manager Headless Server Profile spec also changes. Whichever merges second integrates both additive changes; the headless profile shows the new values through the CLI because both executables share the CLI.
- **Rationale**: Both changes are additive; no ordering dependency is needed.

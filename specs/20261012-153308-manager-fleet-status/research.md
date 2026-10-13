# Research: Analyst Manager Status Report and Fleet Read API

## R1. Carrying the report with the registration check

- **Decision**: A new operation `POST /api/v1/analyst-managers/self/status`
  with the report as the body. It returns the existing own-registration
  information plus `statusCheckIntervalSeconds`. The Manager's
  `RegistrationStatusMonitor` uses it for every check while the registration is
  Active. While Unregistering it keeps using
  `GET /api/v1/analyst-managers/self/registration`, which also gains
  `statusCheckIntervalSeconds`.
- **Rationale**: A `GET` cannot carry a body cleanly. One request per interval
  keeps "with the existing check" literally true. The response stays the same
  shape, so revocation observation is unchanged.
- **Alternatives considered**: A separate report call on the same schedule
  doubles requests and can disagree with the check.

## R2. Validation without blocking the check

- **Decision**: The platform validates the report:
  - `operatingState` is `running`, `paused`, `runtime-unavailable`, or
    `draining`;
  - `pausedUntil` is present only for `paused`;
  - `runtimeUnavailableReason` is present only for `runtime-unavailable`, at
    most 128 characters;
  - `0 ≤ activeAnalysts ≤ maxAnalysts ≤ 1024`;
  - `managerVersion` is at most 64 characters.

  An invalid report is not stored. The response still returns the registration
  information with `reportAccepted: false` and the violated fields (FR-003).
  The Manager logs the refusal and keeps checking.

## R3. Storage and deletion

- **Decision**: `registry.analyst_manager_status` holds one row per
  `registration_id` (PK and FK), upserted with the platform's `received_at`.
  `RevokeRegistration` and `CompleteUnregistration` delete the row in their
  existing unit of work (clarification). Inactive registrations never reach
  the upsert, because Manager authentication refuses them.

## R4. Connectivity

- **Decision**: Options `Registry:StatusReporting`:
  - `Interval` (default 60 s);
  - `MissedIntervalsBeforeOffline` (default 3).

  When the fleet is read:
  - **online**: the registration is Active or Unregistering and
    `now − received_at ≤ Interval × MissedIntervalsBeforeOffline`;
  - **offline**: otherwise, including never reported;
  - **not applicable**: the registration is not Active or Unregistering.

  The row records the interval in force when the report was received
  (`interval_seconds`), so a changed interval does not flip connectivity
  retroactively (edge case).

## R5. Registrar view

- **Decision**: `ListRegistrationsHandler` and `GetRegistrationHandler` first
  try `ClubPermission.Administer`. Otherwise, with `ClubPermission.Register`,
  they filter by `submitted_by = current member` and omit `status` and
  `connectivity`. A request submitted by another member returns `404`, like an
  unknown one (FR-007). Members with neither permission get `403`. Registration
  rows created before submission (pairing only) are invisible to Registrars.

## R6. Manager report content

- **Decision**: `StatusReportBuilder` reads:
  - `OperatingStateMachine` (state and pause end);
  - the latest `PreflightResult` (the first failed check code as
    `runtimeUnavailableReason`);
  - `AttemptRegistry.ActiveCount`;
  - `ConcurrencyLimit.Effective` (from Analyst Manager Work Execution);
  - the assembly version.

  `Ended` and transitional states are not reported, because the Manager only
  checks while Active.

## R7. Host label

- **Decision**: The CLI's existing `--host-label` is validated locally (1–64
  characters, no control characters, trimmed) before the registration request.
  `DeviceMetadata` on the platform adds the control-character refusal. The
  Registrar's submission response already returns `deviceMetadata.hostLabel`
  and the fingerprint; tests now assert it (FR-010, FR-011). The tray's
  register action keeps no label prompt (out of scope; the CLI is the
  specified path).

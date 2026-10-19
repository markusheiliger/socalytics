# Research: Analyst Manager Minimum Version

## R1. Configuration and validation

- **Decision**: A new options class `ManagerVersionOptions` (section `Registry:ManagerVersion`, property `Minimum`,
  default empty = no minimum) in `src/platform/SocAlytics.Platform.Application/Registry/ManagerVersions/`, bound with
  `BindConfiguration`, validated with `ValidateOnStart` (empty or `MAJOR.MINOR.PATCH`, each part a non-negative
  integer without leading sign or suffix, at most 64 characters) so startup fails on an invalid value (FR-001, FR-002).
  A singleton `MinimumManagerVersionProvider` reads the value through `IOptionsMonitor<ManagerVersionOptions>` and
  keeps the last valid value: on reload it re-validates, and an invalid reloaded value is logged once and ignored
  (FR-003, clarification). Every exchange and refusal reads `provider.Current`, so a reload or a restart applies a
  change without re-registration.
- **Rationale**: Mirrors `WebUi:MinimumVersion` (archived [Web UI PWA updates](../20261015-194113-web-ui-pwa-updates/research.md),
  R4) and the existing `Registry:*` sections; the monitor satisfies "takes effect while running".
- **Alternatives considered**: `IOptions` only (rejected: needs a restart, contradicts the clarification); a database
  setting with an admin API (rejected: out of scope, the architecture names deployment configuration).

## R2. Version form and comparison

- **Decision**: A domain value `ManagerVersion` (`src/platform/SocAlytics.Platform.Domain/Registry/ManagerVersion.cs`)
  parses `MAJOR.MINOR.PATCH` and compares the three integers in order. `ManagerVersion.IsBelow(string? reported,
  ManagerVersion minimum)` returns true when `reported` is missing or not well formed (FR-012, edge case). The Manager
  has its own equivalent comparer in Core (`src/analysts/manager/SocAlytics.Analysts.Manager.Core/Registration/ManagerVersionComparer.cs`);
  a golden fixture `contracts/analyst-manager/status/v1/minimum-version.fixtures.json` (beside the schema, like
  `registration/v1/fixtures.json`) pins the shared cases (equal, lower patch, higher minor, malformed, no minimum) for
  both test suites.
- **Rationale**: The Manager reports `Assembly.GetName().Version.ToString(3)` today; prerelease suffixes are out of
  scope (spec Assumptions).

## R3. One build version in the Manager

- **Decision**: Today pairing claims the constant `RegistrationCoordinator.ManagerVersion = "0.1.0"` while the status
  report sends the Core assembly version (`1.0.0` without a `<Version>` property). A singleton `ManagerBuildVersion`
  (`Core/Diagnostics/ManagerBuildVersion.cs`) becomes the only source: it reads the Core assembly version
  (`ToString(3)`), and `SocAlytics.Analysts.Manager.Core.csproj` sets `<Version>0.1.0</Version>` so the value stays the
  one pairing already claims. Pairing metadata, the status report, the status snapshot, and the comparison all use it
  (FR-005); tests construct it with any value.
- **Rationale**: FR-005 requires the compared version to be the reported version; two sources would make the
  platform's and the Manager's decisions disagree.

## R4. Which exchange carries the minimum to the Manager

- **Decision**: The response of `POST /api/v1/analyst-managers/self/status` (the exchange the Manager sends with every
  registration check while active) gains the optional property `minimumManagerVersion` (`string`, or `null` when no
  minimum applies). The status contract `contracts/analyst-manager/status/v1` moves from `1.0.0` to `1.1.0` additively
  (the 1.0.0 schema is kept at `releases/1.0.0.schema.json`). `GET .../self/registration` is unchanged; while it is
  used (no reportable operating state, or unregistering) the Manager keeps its last known minimum.
- **Rationale**: FR-004 asks for an exchange performed at least once per interval; the status report is exactly that
  and the Manager already parses its response in `RegistrationStatusMonitor`. A response header on every Manager
  response (the Web UI pattern) was considered, but the Manager's admission decision is driven by the periodic check,
  and a body property is covered by the contract tests and golden fixtures.
- **Alternatives considered**: queue-credential response (rejected: a Manager below the minimum stops requesting
  credentials, so it would never learn that the minimum was lowered); a header on every `/analyst-managers/self/*`
  response (rejected: duplicate channel, not covered by the JSON Schema contracts).

## R5. Platform enforcement points

- **Decision**: An application service `ManagerVersionGate` (`Application/Registry/ManagerVersions/`) returns
  `OperationFailure.Forbidden("analyst-manager-version-below-minimum")` when a minimum is configured and the
  registration's effective version is below it. It is called by `IssueQueueCredentialsHandler` after the
  `Active`-state check, and by `ClaimLogicalJobHandler` only on the path that would create a new attempt; an
  idempotent replay of a claim granted before the change returns the existing grant (FR-011, FR-014). Heartbeats,
  result grants, upstream reads, completions, status, registration, queue-depth, and unregistration operations never
  call it (FR-013). The problem response is `403` with `type` `urn:socalytics:problem:analyst-manager-version-below-minimum`,
  a fixed title and detail, and the extension member `minimumManagerVersion`; `OperationFailure` gains an optional
  `Extensions` map (default none) that the gate fills, and `ProblemResults` writes those members into the body.
- **Rationale**: Queue credentials are the work-acquisition credential and claims create attempts; refusing both makes
  correctness independent of the Manager (US2). The Manager acknowledges a delivery right after a granted claim, so a
  running attempt needs no new queue credential and is unaffected.
- **Alternatives considered**: refusing claims only (rejected: an outdated Manager could still hold queue access and
  keep redelivering jobs); fencing running attempts (rejected: FR-014).

## R6. Effective Manager version

- **Decision**: `IManagerStatusStore` gains `FindVersionAsync(registrationId)` returning the latest stored report's
  `ManagerVersion`; the gate falls back to `AnalystManagerRegistration.DeviceMetadata.ManagerVersion` (claimed at
  pairing) when no report exists (FR-012). The fleet read uses the same rule with the data it already loads
  (`FleetEntry.Status` or the device metadata).
- **Rationale**: No schema change; the latest report is the Manager's own current claim, pairing metadata covers the
  window before the first report and pending requests (clarification).

## R7. Manager admission, notice, and refusals

- **Decision**: A singleton `MinimumVersionState` (`Core/Registration/MinimumVersionState.cs`) holds the last
  minimum, the build version, and `IsBelow`. `RegistrationStatusMonitor` updates it from each successful status
  response; `AdmissionGate.TryAdmit` returns false while `IsBelow` (no claim, delivered-but-unclaimed jobs are
  negatively acknowledged by the existing `AttemptRunner` path for `NotAdmitted`), and `QueueCredentialProvider` does
  not request credentials while `IsBelow` (FR-006). Running attempts are untouched (FR-007). A version problem response
  on a claim or credential request sets `IsBelow` until the next successful check (FR-010); the claim path
  negatively acknowledges the delivery so other Managers get the job. When the state clears, admission and credential
  requests resume through the existing loops (FR-009).
- **Rationale**: Reuses the admission gate that pause and unregister already use; no new operating state, so the
  status report's `operatingState` contract is unchanged and the notice is shown "in addition to" it (edge case).

## R8. Update notice surfaces

- **Decision**: `ManagerStatusSnapshot` gains `UpdateRequired` (`required`, `managerVersion`, `minimumVersion`). The
  local-control `status` result (`src/analysts/manager/contracts/local-control.schema.json`) gains an optional
  `update` object with those fields; `ManagerCli` prints `Update required: Manager <v> is below the stamp minimum <m>.`
  in plain text and the object in `--json`. The tray `StatusWindow` shows the same sentence above the operating state,
  and `TrayMenuBuilder` adds a disabled status line `Update required (minimum <m>)`; no operating-system notification
  (FR-008, clarification). Texts follow the existing resource conventions of the tray application.

## R9. Fleet read and Web UI

- **Decision**: `AnalystManagerRegistrationDto` gains two operations-only properties, `minimumManagerVersion`
  (`string?`) and `belowMinimumVersion` (`bool`), serialized only when `IncludesOperations` (Club Admin list and detail,
  including pending requests; Registrar views unchanged) through the existing `RegistrationDtoJson` rule (FR-016). The
  OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` is refreshed and the Kiota client
  regenerated. The Web UI maps both in `registrationAdapter.ts` and `models/registrations.ts`; `StatusLines.tsx` renders
  `Update required: version <v>, minimum <m>` as a list item with a text label and an icon that is not the only
  carrier (FR-017); `FleetPage.tsx` and `RegistrationPage.tsx` use it.

## R10. Telemetry

- **Decision**: `AnalysisMetrics` gains the counter `socalytics.analysis.manager_version_refusals` with the single tag
  `operation` (`queue-credentials`, `claim`); handlers log a structured warning with registration id, effective
  version, minimum, and operation (FR-018). No audit event (clarification). The existing meter
  `SocAlytics.Platform.Analysis` is already exported, so `ServiceDefaults` is unchanged.

## R11. Tests and tooling

- **Decision**: Platform Host tests: options validation at start (`ManagerVersionOptionsTests`). Platform
  Architecture tests keep passing. Integration tests in `Registry/`: `ManagerVersionTests` (fixture cases),
  `StatusMinimumVersionTests` (status response carries the minimum; reload, including an invalid reloaded value),
  `MinimumVersionEnforcementTests` (queue credentials and claims refused below the minimum with the problem response
  and metric; heartbeat, result grants, and completion of an attempt claimed before the change succeed), and
  `FleetMinimumVersionTests` (fleet list and detail flags, including a pending request and the Registrar view).
  Contracts tests validate the new examples and the 1.0.0 release copy. Manager tests: comparer, admission and
  credential suppression, refusal handling, snapshot, CLI text and JSON, tray view model and menu line.
  Platform EndToEnd test: a Manager below a raised minimum claims nothing while its running attempt completes, and
  claims again after the minimum is lowered. Web UI: Vitest for the adapter and `StatusLines`, Playwright `simulated`
  fleet scenario with the simulated API.
- **Tooling**: .NET 10, Node 24, Kiota, Playwright Chromium, Docker, and SoftHSM2 are provided by the environment
  actions and `AGENTS.md`; no new framework, SDK, or tool is needed.

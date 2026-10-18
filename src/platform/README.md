# Platform

## Purpose

This area owns the SocAlytics control-plane platform and contains its first
executable .NET 10 host scaffold.

## Ownership

The boundary includes the API/BFF and the ASP.NET Core application, a
well-structured monolith organized in Domain, Application, Infrastructure, and
API layers, that owns platform application behavior and coordinates platform
services.

## Exclusions

Client application source, agent runtimes and tooling, Analyst execution
software, and Analyst capabilities do not belong here.

## Current Status

The executable scope is an ASP.NET Core API that uses PostgreSQL through the
Infrastructure layer, composed by an Aspire AppHost. The AppHost starts
PostgreSQL, then the one-off `SocAlytics.Platform.Migrator`, then the API, in
that order. The API exposes liveness at `/alive`, readiness at `/health`, and
the `v1` OpenAPI document at `/openapi/v1.json`. `/alive` checks the process
only; `/health` includes the `database` check and requires a reachable database
with a current migration state. The solution is split into the layer projects
Domain, Application, Infrastructure, and Api, plus the Migrator; the API wires
the Application and Infrastructure layers through their public composition
methods, and functional areas become folders and namespaces inside the layers.
Host tests cover startup, endpoints, OpenAPI, and composition;
`SocAlytics.Platform.Integration.Tests` covers migrations, units of work,
version triggers, and readiness against disposable PostgreSQL containers;
architecture tests enforce the layer dependency direction and
implementation-type visibility.

The persistence foundation (schema, migrations, Migrator, units of work,
optimistic concurrency, database-aware readiness) is implemented as
development evidence. Further domain behavior, client
applications, deployment images and configuration, production credentials, and
production deployment remain deferred.

The identity foundation is implemented as development evidence: local accounts,
a server-validated BFF session with anti-forgery protection, membership, club
and team roles, team-scoped authorization, security audit events, and the
`Club > Season > Team > Match` hierarchy under `/api/v1`. External OpenID
Connect sign-in is implemented as development evidence against a test
provider (providers under `IdentityAccess:ExternalProviders:<id>` with
`DisplayName`, `Authority`, `ClientId`, `ClientSecret`, `Scopes`, and
`TokenEndpointAuthMethod`, plus `IdentityAccess:ExternalSignIn:WebUiBaseUrl`
and `PendingLifetime`); members are linked only through Club Admin link
credentials, and SocAlytics offers no MFA for local accounts (a club that needs
MFA uses a provider that enforces it). Self-service recovery, clients, Analyst Manager identity,
production values for POL-001, POL-002, and POL-009, deployment, and
production readiness remain deferred.

Recording upload and lineage are implemented as development evidence:
multipart recording upload and recording lineage under `/api/v1`, stored in
a local RustFS (S3-compatible) container that the AppHost starts as a
resource, and a background expiry worker for abandoned uploads.
`Tests/SocAlytics.Platform.Contracts.Tests` validates the machine-readable
contracts under the repository-root `contracts/` folder. Run it with
`dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests`.

Analyst Manager registration is implemented as development evidence (pairing,
approval, activation, DPoP machine tokens, revocation, unregistration):

- Anonymous Manager endpoints: `GET /api/v1/analyst-managers/stamp`,
  `POST /api/v1/analyst-managers/pairings`, `.../pairings/status`,
  `.../pairings/activation`, and `POST /api/v1/analyst-managers/token`.
- Registrar endpoints (`MapMemberApi`): `POST`/`GET`
  `/api/v1/analyst-manager-registrations`, `GET .../{registrationId}`, and
  `POST .../{registrationId}/approval`, `/rejection`, and `/revocation`.
- Manager `self` endpoints: `GET /api/v1/analyst-managers/self/registration`,
  `GET .../self/queue-depth`, `GET .../self/workflow-capabilities`,
  `POST .../self/registry-credentials`, `POST .../self/status`,
  `POST .../self/unregistration`, and `POST .../self/unregistration/completion`;
  they require the `AnalystManagerDPoP` authentication scheme and the
  `AnalystManager` authorization policy. The principal carries the claims in
  `AnalystManagerClaimTypes`: `client_id`, `socalytics:registration_id`, and
  `scope` (`analyst-manager`).
- Status report: an active Manager sends `POST /api/v1/analyst-managers/self/status`
  with its operating state, runtime-unavailable reason, active and maximum
  Analysts, and version. The platform validates it (`ManagerStatusReport`), keeps
  only the latest report per Manager in `registry.analyst_manager_status`
  (migration `0014_registry_analyst_manager_status.sql`), and returns
  `statusCheckIntervalSeconds`; an invalid report is answered with
  `reportAccepted: false` and field violations. Club Admins read `status` and
  `connectivity` (`online`, `offline`, `not-applicable`) in the registration
  list and detail; Registrars read only the registrations they submitted,
  without status. `Registry:StatusReporting:Interval` (60 seconds) and
  `Registry:StatusReporting:MissedIntervalsBeforeOffline` (3) are development
  values, not adopted production values.
- Queue depth: an active Manager reads `GET /api/v1/analyst-managers/self/queue-depth`
  with one `capability` query value per assigned capability (1–64) and receives,
  per capability of the default workflow, the number of ready jobs its JetStream
  consumer `analyst-<id>` has not yet delivered (`waiting`), the `total`, and
  the platform's `observedAt`
  (`contracts/analyst-manager/queue-depth/v1`). Unknown or unprovisioned ids are
  omitted, a registration that is not active gets `403` `manager-not-active`,
  and an unreachable broker gets `503` `urn:socalytics:problem:queue-unavailable`.
  The value is operational guidance, is not audited, and is not part of the
  status report or the fleet read.
- Docker runtime support: an active Manager reads the capability ids, versions,
  and pinned resource requirements of the default workflow from
  `GET /api/v1/analyst-managers/self/workflow-capabilities`
  (`contracts/analyst-manager/workflow-capabilities/v1`) and obtains
  short-lived, read-only registry pull credentials for the default workflow's
  repositories from `POST /api/v1/analyst-managers/self/registry-credentials`
  (`contracts/analyst-manager/registry-credentials/v1`). A registration that is
  not active gets `403` `manager-not-active`; tokens are never logged or
  audited. `Registry:Images:PullCredentialLifetime` (5 minutes) caps the
  credential lifetime and is a development value, not an adopted production
  value.
- `Stamp:Id` and `Stamp:PublicBaseUri` identify the stamp and build the
  verification address (development values are in `appsettings.Development.json`).
- `Registry` option defaults: `SoftwareKeys` `refused` (`headless-server-only`
  accepts software keys from headless server claims, `all` only in development
  and test; other environments fail start-up with `all`), `Pairing:MaxFingerprintMismatches` 3,
  `Pairing:Window` 10 minutes, `Pairing:ApprovalWindow` 24 hours,
  `Pairing:ActivationWindow` 10 minutes, `Pairing:ChallengeLifetime` 60 seconds,
  `Pairing:PollingInterval` 5 seconds, `Tokens:Lifetime` 5 minutes, and an
  expiry worker interval of 30 seconds. These are development values, not
  adopted production values.
- The golden fixtures under `contracts/analyst-manager/` are exercised by the
  fixture tests in `Tests/SocAlytics.Platform.Integration.Tests/Registry` and by
  the Analyst Manager tests.

The Analyst Registry (migration `0015_registry_analyst_registry.sql`) caches
Analyst capability declarations by image digest, holds content-addressed Analyst
profiles (draft, approved, retired), workflow definitions, and the one default
workflow, and resolves analysis runs from the default workflow.

- Registry API under `/api/v1/registry` (Club Admins, `ActiveMember` session,
  anti-forgery on state changes, `If-Match` on approval, retirement, and the
  default workflow): `analyst-images` (register by digest, list, read),
  `analyst-profiles` (create, list, read, `approval`, `retirement`),
  `workflows` (define, list, read), and `default-workflow` (read, set). The
  operations are `registerAnalystImage`, `listAnalystImages`, `getAnalystImage`,
  `createAnalystProfile`, `listAnalystProfiles`, `getAnalystProfile`,
  `approveAnalystProfile`, `retireAnalystProfile`, `defineWorkflow`,
  `listWorkflows`, `getWorkflow`, `getDefaultWorkflow`, and `setDefaultWorkflow`.
  Contracts: `contracts/analysis/analyst-manifest/v1/` and the
  [Registry API contract](../../specs/archive/20261012-152148-analyst-registry/contracts/registry-api.md).
- `Registry:Images` options: `RegistryHost`, `RepositoryPrefix`, optional
  `Username` and `Token` (configured together), and `UseHttp` (Development and
  Test only; HTTPS is required elsewhere). Only digest references within the
  host and prefix are imported; the packaging convention is in
  `docs/architecture/analysts-models-and-hardware.md`. Registry credentials are
  never returned by the API. The integration tests use a disposable `registry:2`
  container.
- Development and Test seed: when the Registry is empty, the hosted
  `ConfiguredWorkflowSeeder` creates schema-valid `source = seed` declarations,
  approved profiles, and a default workflow from `Analysis:DefaultWorkflow`
  (no image registry access) and records one `registry.seeded` audit event.
  Tests that need an empty Registry start the API with `omitWorkflow: true`.
  Without a default workflow, runs resolve as not configured.

Member email addresses and notifications are implemented as development
evidence (migration `0019_identityaccess_email_notifications.sql`):

- Optional member addresses with single-use confirmation links, notification
  preferences, and Club Admin-visible addresses under `/api/v1`.
- A dedicated transactional email outbox table and a delivery worker in the API
  host that submits messages with MailKit over SMTP, with bounded retries.
  Delivery is not on the NATS-published outbox.
- The pending-request notification to Club Admins with a verified address,
  coalesced per recipient.
- The AppHost starts a Mailpit container (`axllent/mailpit` `v1.31.5`) as the
  relay and injects the `Email` options (`Smtp:Host`, `Smtp:Port`,
  `Smtp:Security`, `SenderAddress`, `SenderName`, `Confirmation:Lifetime`,
  `Confirmation:MaxPerHour`, `Confirmation:AttemptsPerMinute`,
  `Delivery:MaxAttempts`, `Delivery:PollInterval`, `Delivery:LeaseDuration`)
  and `Notifications` options (`CoalescingWindow`, `WebUiBaseUri`). These are
  development values, not production values. `StartTlsWhenAvailable` is
  allowed only in Development; the Email options are validated at startup.
- Production relay, sender authentication, and bounce handling remain deferred.
  Integration and Host tests use a disposable Mailpit container and need
  Docker.

The durable analysis workflow is implemented as development evidence (outbox,
NATS JetStream publication, run creation from finalized recording sets,
readiness, fenced attempts, member-initiated run cancellation
(`POST /api/v1/analysis/runs/{runId}/cancellation`, `cancelAnalysisRun`;
migration `0021_analysis_run_cancellation.sql`), Analyst job and completion
contracts). Result
write grants and validated result acceptance are implemented against the
simulated Analyst as development evidence (migrations
`0017_analysis_result_manifests.sql` and
`0018_analysis_completion_outcome_reason.sql`):

- `POST /api/v1/analysis/attempts/{attemptId}/result-grants`
  (`issueAnalysisResultGrants`, Manager DPoP authentication) issues
  short-lived per-object write grants for the attempt's own result location.
- A `succeeded` completion is accepted only after the API reads the manifest
  from object storage and passes the ordered checks (storage-attested
  checksum, schema, scope, provenance, lineage, artifacts, validity window).
  A rejected manifest ends the attempt as failed with a named reason under the
  normal retry rules and writes the `analysis.result-rejected` audit event;
  accepted results get an immutable manifest record. Fact indexing remains
  future work.
- `Analysis:Results` options (development defaults, not production values):
  `GrantLifetime` 10 minutes, `MaxObjectsPerAttempt` 16, `MaxObjectBytes`
  100 MiB, `MaxAttemptBytes` 1 GiB, and `MaxManifestBytes` 1 MiB.
- Contracts: `contracts/analysis/result-manifest/v1/` and
  `contracts/analysis/result-grants/v1/`.

The Manager-mediated upstream read is implemented as development evidence:
`GET /api/v1/analysis/attempts/{attemptId}/upstream-results`
(`listAnalysisUpstreamResults`, Manager DPoP authentication) returns, for the
current unfenced attempt of the owning Manager, exactly the accepted results in
the job's input snapshot for one upstream capability and schema version, with
manifests, short-lived per-object read grants, and coverage per expected
segment (accepted, missing, failed, blocked, skipped). Other runs,
non-upstream capabilities, foreign attempts, and fenced attempts are refused
without disclosure (`analysis.upstream-read-refused` audit events). Options
`Analysis:Reads:GrantLifetime` (10 minutes) and `Analysis:Reads:MaxPageSize`
(100) are development values. Contract:
`contracts/analysis/upstream-results/v1/`.

Real Analyst execution is implemented on the Manager's `docker-desktop`
profile and verified against a Linux engine; Docker Desktop on Windows awaits
manual evidence. Production transport values and production readiness remain
deferred.

- The AppHost starts a `nats` resource (JetStream enabled) and injects
  `ConnectionStrings:nats` into the API. Docker is required for it.
- NATS authentication (`Nats:Authentication`): `Mode` is `None` (default,
  allowed only in the Development and Test environments) or `Jwt`, which
  requires `OperatorSeed`, `AccountSeed` (stamp account), and
  `SystemAccountSeed`. In `Jwt` mode the platform issues per-Manager user JWTs
  that only pull and acknowledge work (migration
  `0016_registry_queue_credentials.sql`) and pushes account revocations through
  the system account, with a retrying revocation worker. Production key custody
  and lifetimes are unresolved under `GOV-CRED-004`.
- `WebUi:MinimumVersion` (default empty): the minimum Web UI release version,
  shaped `YYYY.MMDD.HHMMSS`, validated on start. When set, every `/api/**`
  response carries it in the `SocAlytics-Minimum-Web-Version` header so an older
  Web UI updates itself; the platform never refuses a request by client
  version.
- `AnalystWork` options (development defaults, not production values):
  `MaxConcurrentAnalystsPerManager` 2, `QueueCredentialLifetime` 30 minutes,
  `QueueUrl` `nats://localhost:4222`, `ConsumerAckWait` 30 seconds, and
  `UnfitRedeliveryDelay` 30 seconds.
- `Tests/SocAlytics.Platform.EndToEnd.Tests` runs the platform against a real
  Analyst Manager with a simulated runtime. It needs Docker and SoftHSM2 (the
  Manager's device key) and skips without SoftHSM2:

  ```powershell
  dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests
  ```

- The API hosts the outbox publisher, retention, Job Monitor,
  recordings-finalized consumer, and reconciliation workers. The switch
  `Analysis:Workers:Enabled` (default `true`) turns them off; tests may disable
  the workers and drive the commands directly.
- Health checks: `nats` is registered only when `ConnectionStrings:nats` is
  configured and reports degraded, never unhealthy; `analysis-workflow`
  (FR-046) reports whether the workflow definition resolves.
- Development configuration sections (every value is a development default,
  not a production value): `ConnectionStrings:nats`; `Messaging:Nats`
  (`ProvisionTopology` true in Development, streams `SOCALYTICS_EVENTS` and
  `SOCALYTICS_ANALYSIS_JOBS`, `DuplicateWindowSeconds` 120); `Messaging:Outbox`
  (`PollIntervalSeconds` 1, `BatchSize` 50, `MaxAttempts` 8,
  `BackoffBaseSeconds` 2, `BackoffMaxSeconds` 300, `RetentionPeriodHours` 168,
  `RetentionIntervalMinutes` 60); `Analysis:Notifications` (`ConsumerName`
  `analysis-scheduler-recordings-finalized`, `NakDelaySeconds` 10,
  `ResolveRecheckSeconds` 15); `Analysis:Leases` (`MonitorIntervalSeconds` 5,
  `BatchSize` 50); `Analysis:Reconciliation` (`OnStartup` true, `OnReconnect`
  true, `BatchSize` 200); `Analysis:Workers` (`Enabled` true);
  `Analysis:DefaultWorkflow` (development fixture workflow in
  `appsettings.Development.json`).

The AppHost bootstraps the first club with these parameters:

- `first-club-admin-password`: generated and persisted in the AppHost user
  secrets. Read it with
  `dotnet user-secrets list --project src/platform/SocAlytics.Platform.AppHost`.
- `first-club-admin-account-name`: defaults to `club-admin`.
- `club-display-name`: defaults to `Development Club`.

Interactive sign-in uses the API HTTPS endpoint, which needs the ASP.NET Core
development certificate (`dotnet dev-certs https --trust`). The endpoint is
omitted when the AppHost runs with `--SocAlytics:ApiHttpsEndpoint=false`, as
the host smoke test does. The first club administrator must change the
password at first sign-in; afterwards remove
`ClubBootstrap__FirstClubAdmin__InitialPassword` from the configuration.
Without a valid bootstrap configuration, `/health` stays unhealthy
(`club-not-established` or `bootstrap-conflict`).

Operator break-glass recovery: set `BreakGlassRecovery__AccountName`,
`BreakGlassRecovery__RecoveryId`, and `BreakGlassRecovery__TemporaryCredential`
(from the secret store), start the API, confirm the
`break-glass-recovery.applied` audit event, then remove the directive.
`IdentityAccess` settings exist only in `appsettings.Development.json` and
test configuration.
 This scaffold does not claim production
readiness.

## Development

A running Docker-compatible container runtime is required to run the AppHost
and the Host and Integration tests. Run the supported workflow from the
repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

Run only the integration tests, the contract tests, or the Migrator on its own:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
${env:ConnectionStrings__socalytics-migrator} = '<connection string>'
dotnet run --project src/platform/SocAlytics.Platform.Migrator
```

Local database credentials are generated development-only values kept in the
AppHost user secrets. To reset local data, stop the AppHost and run
`docker volume rm socalytics-postgres-data`. This removes development data
only.

## Migrator Contract

The Migrator applies all pending platform migrations once and exits. It is safe
to run repeatedly and concurrently against the same database. It takes no verbs
or required arguments. Configuration follows the .NET Generic Host order:
`appsettings.json`, `appsettings.{Environment}.json`, environment variables,
then `--Key=value` arguments.

| Key | Required | Default | Rule |
| --- | --- | --- | --- |
| `ConnectionStrings:socalytics-migrator` (`ConnectionStrings__socalytics-migrator`) | yes | none | Npgsql connection string for a login that is, or is a member of, `socalytics_migrator`. |
| `Migrator:ConnectTimeout` | no | `00:01:00` | Positive `TimeSpan`; total time spent retrying the initial connection. |
| `Migrator:LockWaitTimeout` | no | `00:02:00` | Positive `TimeSpan`; bounded wait for another run's migration lock. |
| `Migrator:ScriptTimeout` | no | `00:05:00` | Positive `TimeSpan`; command timeout per migration script. |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | no | none | When set, logs and traces are exported through ServiceDefaults. |

| Code | Name | Meaning |
| --- | --- | --- |
| `0` | `Success` | Database is current. |
| `1` | `UnexpectedError` | Unclassified failure. |
| `2` | `ConfigurationInvalid` | Missing connection string or invalid option. |
| `3` | `DatabaseUnavailable` | No connection within `ConnectTimeout`, or authentication failed. |
| `4` | `CatalogInvalid` | Malformed name, empty script, duplicate sequence or identity. |
| `5` | `ChecksumMismatch` | An applied migration's registered content changed. |
| `6` | `SequenceConflict` | A pending migration is numbered at or below the highest applied sequence. |
| `7` | `MigrationFailed` | A script failed and was rolled back; later scripts were not attempted. |
| `8` | `LockTimeout` | Another run held the lock beyond `LockWaitTimeout`. |
| `9` | `Cancelled` | Shutdown signal received; the in-flight script was rolled back. |

Scripts that completed earlier in the same run stay committed for codes `1`,
`7`, and `9`. Logs never contain connection strings, passwords, script text, or
PostgreSQL server message details.

## Migration Authoring

- Migrations are forward-only files `NNNN_<area>_<description>.sql` under
  `SocAlytics.Platform.Infrastructure/Persistence/Migrations/`, embedded as
  resources. `NNNN` is the next free four-digit number on an up-to-date `main`;
  a feature renumbers before merge if its number was taken. `<area>` matches
  `[a-z][a-z0-9]*`; `<description>` is lowercase words joined by `_`.
- The identity is the file name without `.sql` and never changes. The checksum
  is `sha-256:` plus the hex SHA-256 of the BOM-stripped, LF-normalized content.
  Never edit or delete an applied migration; add a new one.
- Each migration runs in its own transaction with its history record. Do not
  issue `COMMIT`, `ROLLBACK`, or statements that cannot run in a transaction.
- Use schema-qualified names (`socalytics.<table>`). Do not create schemas,
  roles, or credentials, or grant privileges to roles other than
  `socalytics_app`. Never add `club_id`, `tenant_id`, or another cross-club
  discriminator.
- A mutable aggregate root has `version bigint NOT NULL DEFAULT 1` and calls
  `CALL socalytics.attach_version_trigger('socalytics.<root>');`. A child table
  whose rows always change with its root has no `version` column and calls
  `CALL socalytics.attach_aggregate_child_triggers('socalytics.<child>', 'socalytics.<root>', '<child_key_column>');`
  with a fourth argument when the root key is not `id`. Triggers are named with
  the bare table name.
- Every table without a `version` column needs an entry (`ChildOf(…)`,
  `Immutable`, or `Unversioned(reason)`) in
  `Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs`.
- Data migrations advance the version of each changed versioned row by one. A
  migration that must not do so starts with
  `SET LOCAL socalytics.suppress_version = 'on';`, which lasts for that
  transaction and is honored only for members of `socalytics_migrator`.
- Edit handlers run `UPDATE … WHERE id = @Id AND version = @ExpectedVersion RETURNING version`
  through `VersionedWrites` inside an `IUnitOfWork` scope; a conflict means
  `412` where `If-Match` is required. Lifecycle transitions use
  `WHERE id = @Id AND state = @ExpectedState RETURNING version`; a conflict
  means `409`. Never increment `version` manually as the only change mechanism
  or derive meaning from the size of a version step.

## Authoritative Architecture

- [Architecture overview](../../docs/architecture/overview.md)
- [Platform implementation](../../docs/architecture/platform-implementation.md)
- [Contracts and compatibility](../../docs/architecture/contracts-and-compatibility.md)
- [Job processing](../../docs/architecture/job-processing.md)

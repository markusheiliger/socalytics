# Feature Specifications

Each folder is one [Spec Kit](https://github.com/github/spec-kit)
feature created by `/speckit-specify`. A feature folder holds `spec.md` (what
and why), and later `plan.md`, design artifacts, and `tasks.md` from
`/speckit-plan` and `/speckit-tasks`. The governing design stays in
[`docs/architecture/`](../docs/architecture/README.md); specs cite it instead of
restating it, as required by the
[constitution](../.specify/memory/constitution.md).

New specs are authored with `/speckit-gha-author` in Copilot Chat; see
[Workflow Per Feature](#workflow-per-feature).

## Platform Foundation

Implement these features in dependency order. Start a feature only after the
features it depends on are merged.

| Feature | Purpose | Depends on |
| --- | --- | --- |
| [Environment Verification Coverage](archive/20261007-115855-environment-verification-coverage/spec.md) | Environment feature: automated verification also covers the repository-root `contracts/` folder and the Analyst Manager solution | None |
| [Platform Persistence Foundation](archive/20261005-130700-platform-persistence-foundation/spec.md) | Shared durable storage, one ordered platform-wide migration sequence, transactions and optimistic concurrency, and database-aware readiness | None |
| [Club and Identity Foundation](archive/20261005-130701-club-identity-foundation/spec.md) | Club, season, team, and match hierarchy with authenticated sessions and role-based access | Platform Persistence Foundation |
| [Recording Lineage and Upload](archive/20261005-130702-recording-lineage-upload/spec.md) | Direct-to-storage recording upload, immutable lineage, and authorized finalization | Environment Verification Coverage, Platform Persistence Foundation, Club and Identity Foundation |
| [Analyst Manager Registration](archive/20261005-130704-analyst-manager-registration/spec.md) | Device-bound Analyst Manager registration, safe restore, revocation, runtime preflight, and local operating controls | Environment Verification Coverage, Platform Persistence Foundation, Club and Identity Foundation, Recording Lineage and Upload |
| [Durable Analysis Workflow](archive/20261005-130703-durable-analysis-workflow/spec.md) | Durable analysis runs, readiness evaluation, fenced attempts, reliable work publication, and versioned job contracts | Environment Verification Coverage, Platform Persistence Foundation, Club and Identity Foundation, Recording Lineage and Upload, Analyst Manager Registration |
| [Analyst Manager Work Execution](20261012-141233-manager-work-execution/spec.md) | A registered Analyst Manager pulls, claims, heartbeats, and completes analysis work with a simulated Analyst; per-Manager short-lived queue credentials, concurrency limits, and revocation that fences active attempts | Analyst Manager Registration, Durable Analysis Workflow |

Environment Verification Coverage and Platform Persistence Foundation can be
developed in parallel. Analyst Manager Registration follows Recording Lineage
and Upload, so each takes the next free migration number on an up-to-date
default branch and extends the `contracts/` folder that Recording creates. Durable
Analysis Workflow starts after both are merged, because its Manager operations
use the Analyst Manager authentication.

### Cross-Feature Interfaces

Each plan defines the interfaces it provides; later plans consume them by these
names and do not redefine them.

| Interface | Provided by | Consumed by |
| --- | --- | --- |
| `IUnitOfWork` / `IUnitOfWorkScope`, internal `IDbSession`, `VersionedWriteResult`, version trigger helpers (`attach_version_trigger`, `attach_aggregate_child_triggers`), `PersistedTableClassifications`, the Migrator, `SocAlytics.Platform.Integration.Tests` with `PostgresContainerFixture`, `IsolatedDatabase`, `CapturingLoggerProvider`, `VersionedWrites`, `TestMigrationCatalogs`, and `MigratorHarness`; the AppHost `<UserSecretsId>` | Platform Persistence Foundation | all platform features |
| `IRequestContext`, `IAuditTrail` (`PostgresAuditTrail` details allow-list, named check `ck_security_audit_event_actor_kind`), `OperationResult<T>` / `OperationFailure`, `ITeamScopeResolver`, `ITeamScopeSource`, `IAccessAuthorizer`, `MapMemberApi`, `SessionAntiforgeryFilter`, `ProblemResults`, test helpers `PlatformApiFactory`, `ApiSession`, `TestMembers`, `PlatformServices`, session cookie, `X-CSRF-Token`, `urn:socalytics:problem:<code>` errors, `member_account` / `team` / `match` tables | Club and Identity Foundation | Recording Lineage and Upload, Durable Analysis Workflow, Analyst Manager Registration |
| Repository-root `contracts/` with its index, `$id` convention, `ContractCatalog`, and `SocAlytics.Platform.Contracts.Tests`; `IObjectStorage`; `IRecordingSetLookup` / `RecordingSetLineage`; `recording_finalized_events`; `socalytics.reject_immutable_change()`; `Sha256Digest`; `RustFsContainerFixture`; `ContractCatalog` (`Schemas`, `IndexRows`, `Releases`, `LoadRegistry()`), `ContractIndexTests`, `SchemaMetaValidationTests` (Recording creates `contracts/`; later features extend it); `FinalizeRecordingSetHandler`; test helpers `ClubHierarchyBuilder` and `RecordingFinalizationHelper`; shared idempotency problem codes `idempotency-key-missing` (400) and `idempotency-key-reused` (409) in `SharedProblemCodes` | Recording Lineage and Upload | Analyst Manager Registration and Durable Analysis Workflow (contracts); Durable Analysis Workflow (lineage, event records) |
| `IOutbox`, `outbox_messages`, NATS JetStream publication, Analyst job and completion contracts | Durable Analysis Workflow | later Analyst execution features |
| `AnalystManagerDPoP` scheme (`AnalystManagerDPoPDefaults`), `AuthorizationPolicyNames.AnalystManager`, `AnalystManagerClaimTypes` (`client_id`, `socalytics:registration_id`, `scope` = `analyst-manager`), registration golden fixtures under `contracts/analyst-manager/`, test helper `Registry/Support/TestAnalystManager.cs` and the registration operation ids, central `Microsoft.Extensions.TimeProvider.Testing` 10.10.0 pin | Analyst Manager Registration | Durable Analysis Workflow (Manager operations); the fixtures also the Analyst Manager solution |

This table is a human-readable overview. The order that GitHub automation uses
lives in the native "blocked by" dependencies of each feature's spec twin issue
(label `speckit:spec`), which the `Spec Kit prepare` workflow infers once when
it creates the twin. Each twin also carries a generated `speckit:stage:*` label
showing how far the feature has progressed on the default branch; see the repository
[README](../README.md#spec-twins).

## Archive

Implemented features move to [`archive/`](archive/) under the same folder
name, in the commit that merges their implementation: the implementation
automation does it in its merge, and a local or manual merge runs
`/speckit-gha-archive` first. The move rewrites every reference to the folder
(Markdown links and plain-text paths such as code comments), and the spec twin
follows to the archive after the merge. Archived features stay in the tables
above with links into the archive.

## Folder Naming

Feature folders are named `<YYYYMMDD-HHMMSS>-<short-name>`, where the prefix
is the creation time in **UTC**. Timestamps instead of sequential numbers keep
folder names unique when several developers create features on separate
branches, and the prefix also sorts features by creation time. The prefix
carries no dependency meaning; the tables in this file record dependency
order.

`/speckit-specify` must use the current UTC time for the prefix. Spec Kit's
helper scripts use the machine's local time, so set the environment variable
`TZ=UTC` in the shell when running them directly.

## Workflow Per Feature

### Author

Run `/speckit-gha-author` in GitHub Copilot Chat from the repository root.
Without input it proposes the next logical spec from the architecture, the
existing specs, and the code; with an architecture section (for example
`docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui`) it
authors a spec for that part; with a feature idea in free text it first maps the
idea to the architecture and says when the architecture must change with it;
with a spec folder it resumes where the spec stopped. It runs the Spec Kit skills in their order and pauses for you after
each stage:

1. `/speckit-specify` creates the folder and `spec.md`, and
   `/speckit-clarify` resolves the open questions with you.
2. `/speckit-plan` selects the technical approach from the architecture and
   checks it against the constitution. When the plan needs new frameworks,
   SDKs, or tools, an environment spec is authored and merged first.
3. Optionally `/speckit-checklist` adds a domain checklist.
4. `/speckit-tasks` writes `tasks.md`; its `/speckit-gha-refine` hook
   makes the parallel task groups safe.
5. `/speckit-analyze` checks the artifacts against each other; findings are
   fixed through the Spec Kit skills.
6. The publish checks require stage `tasked`, no open clarification markers or
   checklist items, a row in the tables above, and a clean Markdown check.
7. Your last decision: commit and push the spec folder and this file.

`node .github/scripts/speckit-author.mjs status [folder]` shows where a spec
stands and what still blocks publishing it.

### Publish And Implement

Once the spec is on the default branch, the `Spec Kit prepare` workflow
creates its twin issue and infers its dependencies on other twins; check them
on GitHub. Then either request implementation on GitHub or implement locally.
`/speckit-implement` asks which one you want; `/speckit-gha-request`
requests it directly (see the repository
[README](../README.md#requesting-implementation-on-github)).

- **On GitHub:** the request gets a `speckit/<folder>` branch and a draft pull
  request that closes the twin when merged. The workflows implement the tasks
  phase by phase, converge, archive the spec, and merge it, or ask for your
  review when automation cannot continue.
- **Locally:** run `/speckit-implement` and `/speckit-converge` until
  convergence reports completion, then `/speckit-gha-archive` before the
  merge.

The skills locate the active feature through the machine-local
`.specify/feature.json`, which `/speckit-specify` writes. To work on another
folder, point it there, for example
`{ "feature_directory": "specs/<folder>" }`, set the
`SPECIFY_FEATURE_DIRECTORY` environment variable, or pass the folder to
`/speckit-gha-author`.

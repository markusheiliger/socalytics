# Feature Specifications

Each folder is one [Spec Kit](https://github.com/github/spec-kit)
feature created by `/speckit-specify`. A feature folder holds `spec.md` (what
and why), and later `plan.md`, design artifacts, and `tasks.md` from
`/speckit-plan` and `/speckit-tasks`. The governing design stays in
[`docs/architecture/`](../docs/architecture/README.md); specs cite it instead of
restating it, as required by the
[constitution](../.specify/memory/constitution.md).

## Platform Foundation

Implement these features in dependency order. Start a feature only after the
features it depends on are merged.

| Feature | Purpose | Depends on |
| --- | --- | --- |
| [Environment Verification Coverage](20261007-115855-environment-verification-coverage/spec.md) | Environment feature: automated verification also covers the repository-root `contracts/` folder and the Analyst Manager solution | None |
| [Platform Persistence Foundation](20261005-130700-platform-persistence-foundation/spec.md) | Shared durable storage, one ordered platform-wide migration sequence, transactions and optimistic concurrency, and database-aware readiness | None |
| [Club and Identity Foundation](20261005-130701-club-identity-foundation/spec.md) | Club, season, team, and match hierarchy with authenticated sessions and role-based access | Platform Persistence Foundation |
| [Recording Lineage and Upload](20261005-130702-recording-lineage-upload/spec.md) | Direct-to-storage recording upload, immutable lineage, and authorized finalization | Environment Verification Coverage, Platform Persistence Foundation, Club and Identity Foundation |
| [Durable Analysis Workflow](20261005-130703-durable-analysis-workflow/spec.md) | Durable analysis runs, readiness evaluation, fenced attempts, reliable work publication, and versioned job contracts | Environment Verification Coverage, Platform Persistence Foundation, Recording Lineage and Upload, Analyst Manager Registration |
| [Analyst Manager Registration](20261005-130704-analyst-manager-registration/spec.md) | Device-bound Analyst Manager registration, safe restore, revocation, runtime preflight, and local operating controls | Environment Verification Coverage, Platform Persistence Foundation, Club and Identity Foundation |

Analyst Manager Registration can be developed in parallel with Recording
Lineage and Upload; Durable Analysis Workflow starts after both are merged,
because its Manager operations use the Analyst Manager authentication.

### Cross-Feature Interfaces

Each plan defines the interfaces it provides; later plans consume them by these
names and do not redefine them.

| Interface | Provided by | Consumed by |
| --- | --- | --- |
| `IUnitOfWork` / `IUnitOfWorkScope`, `VersionedWriteResult`, version trigger helpers, `PersistedTableClassifications`, the Migrator, `SocAlytics.Platform.Integration.Tests` | Platform Persistence Foundation | all platform features |
| `IRequestContext`, `IAuditTrail`, `OperationResult<T>`, `ITeamScopeResolver`, `IAccessAuthorizer`, session cookie, `X-CSRF-Token`, `urn:socalytics:problem:<code>` errors, `member_account` / `team` / `match` tables | Club and Identity Foundation | Recording Lineage and Upload, Durable Analysis Workflow, Analyst Manager Registration |
| Repository-root `contracts/` with its index and `$id` convention, `SocAlytics.Platform.Contracts.Tests`, `IObjectStorage`, `IRecordingSetLookup`, `recording_finalized_events` | Recording Lineage and Upload | Durable Analysis Workflow (contracts also Analyst Manager Registration) |
| `IOutbox`, `outbox_messages`, NATS JetStream publication, Analyst job and completion contracts | Durable Analysis Workflow | later Analyst execution features |
| `AnalystManagerDPoP` authentication scheme, `AnalystManager` policy, registration golden fixtures under `contracts/` | Analyst Manager Registration | Durable Analysis Workflow (Manager operations), the Analyst Manager solution |

This table is a human-readable overview. The order that GitHub automation uses
lives in the native "blocked by" dependencies of each feature's spec twin issue
(label `speckit:spec`), which the `Spec Kit prepare` workflow infers once when
it creates the twin. Each twin also carries a generated `speckit:stage:*` label
showing how far the feature has progressed on `main`; see the repository
[README](../README.md#spec-twins).

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

Create a branch named after the feature folder, then run the Spec Kit skills
in GitHub Copilot from the repository root:

1. `/speckit-clarify` resolves the open questions the spec still carries.
2. `/speckit-plan` selects the technical approach from the architecture and
    checks it against the constitution.
3. `/speckit-tasks`, then optionally `/speckit-analyze`.
4. Merge the spec, plan, and tasks to `main`, then either implement locally or
    request implementation on GitHub. `/speckit-implement` asks which one you
    want; `/speckit-gha-request` requests it directly (see the repository
    [README](../README.md#requesting-implementation-on-github)). A GitHub
    request gets a `speckit/<folder>` branch and a draft pull request that
    closes the twin when merged; the workflow implements the tasks one at a
    time, in order, and requests your review when all are done. For local work,
    run `/speckit-implement` and `/speckit-converge` until convergence reports
    completion.

The skills locate the active feature through the machine-local
`.specify/feature.json`. Point it at the folder you are working on, for example
`{ "feature_directory": "specs/20261005-130700-platform-persistence-foundation" }`, or set
the `SPECIFY_FEATURE_DIRECTORY` environment variable.

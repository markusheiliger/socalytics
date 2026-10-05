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
| [Platform Persistence Foundation](20261005-130700-platform-persistence-foundation/spec.md) | Shared durable storage, ordered module migrations, data isolation, and database-aware readiness | None |
| [Club and Identity Foundation](20261005-130701-club-identity-foundation/spec.md) | Club, season, team, and match hierarchy with authenticated sessions and role-based access | Platform Persistence Foundation |
| [Recording Lineage and Upload](20261005-130702-recording-lineage-upload/spec.md) | Direct-to-storage recording upload, immutable lineage, and authorized finalization | Platform Persistence Foundation, Club and Identity Foundation |
| [Durable Analysis Workflow](20261005-130703-durable-analysis-workflow/spec.md) | Durable analysis runs, readiness evaluation, fenced attempts, reliable work publication, and versioned job contracts | Platform Persistence Foundation, Recording Lineage and Upload |
| [Analyst Manager Registration](20261005-130704-analyst-manager-registration/spec.md) | Device-bound Analyst Manager registration, safe restore, revocation, runtime preflight, and local operating controls | Platform Persistence Foundation, Club and Identity Foundation |

Durable Analysis Workflow and Analyst Manager Registration can be developed in
parallel once their dependencies are merged.

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
4. `/speckit-implement` and `/speckit-converge` until convergence reports
    completion.

The skills locate the active feature through the machine-local
`.specify/feature.json`. Point it at the folder you are working on, for example
`{ "feature_directory": "specs/20261005-130700-platform-persistence-foundation" }`, or set
the `SPECIFY_FEATURE_DIRECTORY` environment variable.

<!--
Sync Impact Report
- Version change: template → 1.0.0
- Added principles: I. Architecture Is the Design Authority; II. Respect Source-Area
  Ownership; III. API-First Control Plane; IV. Evidence Over Claims; V. Focused,
  Minimal Changes
- Added sections: Technology and Tooling Constraints; Development Workflow and
  Quality Gates; Governance
- Templates requiring updates: none (plan-template.md derives its Constitution
  Check gates from this file; spec-template.md and tasks-template.md unchanged)
- Follow-up TODOs: none
-->

# socAlytics Constitution

## Core Principles

### I. Architecture Is the Design Authority

`docs/architecture/` is authoritative for the coherent current system design.
Specifications and plans MUST conform to the architecture narratives they
touch. When a feature requires a different design, the plan MUST update the
owning architecture narrative in the same change rather than diverging
silently. Architecture decision records are exceptional and follow
`docs/architecture/decisions/README.md`. Unresolved decisions MUST stay
explicit; provisional values and target-state descriptions MUST NOT be
presented as adopted facts.

### II. Respect Source-Area Ownership

`src/platform/`, `src/clients/`, `src/agents/`, and `src/analysts/` are the
only first-level source areas, each with the ownership and exclusions defined
in its README. A feature MUST place code in the area that owns it and MUST NOT
add another immediate child of `src/` without an accepted architecture change.
Nested source, test, or documentation directories are added only with their
first meaningful artifacts; empty placeholders are not allowed.

### III. API-First Control Plane

Every capability is exposed through APIs, and user interfaces and agents are
API consumers that never hold business logic or access databases directly.
The control plane handles metadata only and never proxies video bytes. Object
storage is used through the S3 protocol without depending on a specific
implementation. Machine-readable contracts (OpenAPI 3.1, JSON Schema 2020-12)
are authoritative over generated code, and component-local executable
contracts live with the component that owns and validates them.

### IV. Evidence Over Claims

Behavior is complete only when it is backed by executable evidence: building
code and passing automated tests. Platform changes MUST keep the host and
architecture test suites passing, and new behavior MUST add focused tests in
`src/platform/Tests` (or the owning area's test location). Successful local
restore, build, test, or AppHost execution MUST NOT be presented as deployment
support or production readiness.

### V. Focused, Minimal Changes

Changes stay scoped to the feature being delivered. Add only files and
dependencies justified by the adopted architecture and the current
specification; avoid speculative abstractions. Preserve unrelated user changes
and never use destructive Git operations.

## Technology and Tooling Constraints

- The platform uses the .NET 10 SDK pinned by `src/platform/global.json` and
  the solution `src/platform/SocAlytics.Platform.slnx`.
- The supported platform commands, run from the repository root, are
  `dotnet restore src/platform/SocAlytics.Platform.slnx`,
  `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`,
  `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, and
  `dotnet run --project src/platform/SocAlytics.Platform.AppHost`.
- The Aspire AppHost is the supported local entry point.
- Technologies the architecture lists as deferred (for example PostgreSQL with
  Dapper and DbUp, NATS, S3-compatible storage, authentication) are introduced
  only by a feature whose specification and plan adopt them.
- `.gitignore` entries are derived from the actual stack and tooling, not
  generic catch-alls.

## Development Workflow and Quality Gates

- Features follow the Spec Kit flow: `/speckit-specify`, optional
  `/speckit-clarify`, `/speckit-plan`, `/speckit-tasks`, optional
  `/speckit-analyze`, `/speckit-implement`, and `/speckit-converge`.
- After each substantive edit, run the narrowest relevant check before
  widening scope.
- Every supported setup, build, test, and lint command is documented in
  `README.md` when it becomes available, and `AGENTS.md` stays current with
  repository conventions.
- Documentation changes pass `node .github/scripts/check-markdown.mjs`.
- Product CI workflows are added only for executable build, lint, or test
  commands that exist.
- Agents never commit, push, configure remotes, or publish without an explicit
  request.

## Governance

This constitution governs specifications, plans, tasks, and implementation in
this repository; plans MUST pass its Constitution Check or justify each
violation in their Complexity Tracking section. `AGENTS.md` provides runtime
guidance for coding agents and MUST stay consistent with this document.

Amendments are made through `/speckit-constitution` (or a reviewed edit of this
file) and MUST update dependent templates and guidance in the same change.
Versioning follows semantic versioning: MAJOR for removed or redefined
principles, MINOR for new principles or sections, PATCH for clarifications.

**Version**: 1.0.0 | **Ratified**: 2026-10-05 | **Last Amended**: 2026-10-05

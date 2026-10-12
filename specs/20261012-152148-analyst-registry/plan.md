# Implementation Plan: Analyst Registry: Manifests, Profiles, and Workflow Resolution

**Branch**: `20261012-152148-analyst-registry` | **Date**: 2026-10-12 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261012-152148-analyst-registry/spec.md`

## Summary

Add a durable Registry to the platform's existing Registry functional area:

- **Manifest import**: an OCI Distribution client reads only the image
  manifest, the image configuration, and the last layer (which by packaging
  convention holds only `analyst-manifest.json`). It verifies every digest,
  checks the `io.socalytics.*` labels against the file, validates the file
  against the new contract `contracts/analysis/analyst-manifest/v1`, and stores
  the declaration immutably by image digest.
- **Profiles**: content-addressed profiles (RFC 8785 canonical JSON, SHA-256)
  with the states draft, approved, and retired.
- **Workflows**: versioned workflow definitions built from approved profiles,
  with dependency validation from the declarations, and one default workflow.
- **Run creation**: a `RegistryWorkflowDefinitionResolver` replaces the
  configured resolver. It produces the same `ResolvedWorkflowDefinition`, so
  run snapshots and job contracts are unchanged.
- **Seed**: in Development and Test, a startup seeder turns the configured
  definition into approved profiles and a default workflow when the Registry
  is empty.
- **API**: Club Admin operations under `/api/v1/registry/…`, with audit
  events.

Design rationale: [research.md](research.md). Entities:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`), nullable
enabled, warnings as errors.

**Primary Dependencies**:

- Existing: ASP.NET Core minimal APIs, Npgsql, Dapper, DbUp, the unit of work,
  `IAccessAuthorizer`, `IAuditTrail`, `MapMemberApi`, `SessionAntiforgeryFilter`,
  `IfMatchHeader`, `ProblemResults`, `JsonSchema.Net` 8.0.5 (already used by
  the contract tests and the analysis contracts), and Testcontainers.
- In-box: `System.Formats.Tar`, `System.IO.Compression`, and
  `System.Security.Cryptography` for layer extraction and digests.
- No new NuGet packages.

**Storage**: PostgreSQL. Migration `NNNN_registry_analyst_registry.sql` takes
the next free number at implementation time; `0015` at planning time, because
`0014` is planned by Analyst Manager Work Execution. It creates the tables in
[data-model.md](data-model.md) with immutability triggers and
`socalytics.attach_version_trigger` on mutable rows.

**Testing**: Integration tests with PostgreSQL and a disposable OCI registry
container (`registry:2`) through Testcontainers. Test images are published
over the OCI Distribution API by a test helper (blobs plus manifest), so no
Docker build is needed. Contract tests validate the new schema and examples;
architecture tests keep Registry infrastructure internal.

**Target Platform**: Linux containers (platform API).

**Project Type**: Web service (platform API).

**Performance Goals**: SC-004: registration reads at most the manifest, the
configuration, and one small layer.

**Constraints**:

- Digest-only references.
- Only the configured image registry host and repository prefix.
- Content verified against digests.
- Immutable declarations, approved profiles, and workflow versions.
- Run snapshots and job contracts unchanged.
- The configured definition is used only for seeding in Development and Test.

**Scale/Scope**: Dozens of images, profiles, and workflow versions per stamp.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements the "queryable Analyst manifest cache keyed by immutable image digest" (`platform-implementation.md`), the manifest packaging (`analysts-models-and-hardware.md` § Image Metadata And Packaging), approved profiles (§ Analyst Profiles), and declarations (§ Capability Declaration). The packaging convention (manifest file in the last layer, plus labels) makes the architecture concrete; `analysts-models-and-hardware.md` § Image Metadata And Packaging records it in this change. | Pass |
| II. Source-area ownership | Only `src/platform` (Registry functional area across the layers) and the repository-root `contracts/`. | Pass |
| III. API-first control plane | All operations are REST under `/api/v1/registry`; the platform reads image metadata only, never model weights or video; the manifest contract is JSON Schema 2020-12 in `contracts/`. | Pass |
| IV. Evidence over claims | Integration tests against a real OCI registry container and PostgreSQL; contract tests; snapshot equality tests; no production promotion claimed. | Pass |
| V. Focused, minimal changes | No new packages; the OCI client covers only the GET operations needed; no UI and no signature verification. | Pass |
| Tooling constraints | Needs .NET and Docker (Testcontainers), already provided by `environment-setup`; no environment feature needed. | Pass |

Post-design re-check: passes.

## Project Structure

### Documentation (this feature)

```text
specs/20261012-152148-analyst-registry/
├── plan.md
├── research.md          # R1–R9
├── data-model.md        # Tables, states, immutability
├── quickstart.md
├── contracts/
│   ├── analyst-manifest-and-packaging.md   # Manifest contract summary, labels, last-layer convention
│   └── registry-api.md                     # REST operations, refusals, audit events
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
contracts/analysis/analyst-manifest/v1/           # analyst-manifest.schema.json, releases/1.0.0.schema.json, examples/valid, examples/invalid
contracts/README.md                               # + index row

src/platform/
├── SocAlytics.Platform.Domain/Registry/           # AnalystProfile (states, transitions), CapabilityDeclaration, WorkflowDefinition validation (cycles, dependencies)
├── SocAlytics.Platform.Application/Registry/
│   ├── Images/                                    # RegisterAnalystImage, IOciImageReader, IAnalystManifestValidator, ICapabilityDeclarationStore
│   ├── Profiles/                                  # Create/Approve/Retire profile, IAnalystProfileStore, ProfileDigest
│   ├── Workflows/                                 # DefineWorkflow, SetDefaultWorkflow, IWorkflowDefinitionStore
│   └── RegistryAuditEvents.cs                     # + new event types
├── SocAlytics.Platform.Infrastructure/
│   ├── Registry/Oci/                              # OciDistributionClient (token auth, manifests, blobs), OciImageReader, ImageRegistryOptions
│   ├── Registry/RegistryWorkflowDefinitionResolver.cs   # replaces ConfiguredWorkflowDefinitionResolver as the registered resolver
│   ├── Registry/ConfiguredWorkflowSeeder.cs       # Development/Test seed of an empty Registry
│   ├── Persistence/Registry/                      # Dapper stores
│   └── Persistence/Migrations/NNNN_registry_analyst_registry.sql
├── SocAlytics.Platform.Api/Registry/AnalystRegistryEndpoints.cs
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/Registry/   # OciRegistryContainerFixture, TestImagePublisher, import/profile/workflow/resolution tests
    ├── SocAlytics.Platform.Contracts.Tests/              # analyst-manifest contract tests
    └── SocAlytics.Platform.Architecture.Tests/           # Registry infrastructure stays internal

docs/architecture/analysts-models-and-hardware.md         # packaging convention
src/platform/README.md, AGENTS.md                          # Current state
```

**Structure Decision**: Extend the existing Registry functional area in all
layers, as the monolith's layering requires. Run creation keeps using
`IWorkflowDefinitionResolver`; only the registered implementation changes.

## Complexity Tracking

No constitution violations.

# Implementation Plan: Capability Fact Payload Schemas

**Branch**: `20261023-010826-fact-payload-schemas` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-010826-fact-payload-schemas/spec.md`

## Summary

An Analyst manifest (new minor contract `1.2.0`) declares a restricted JSON Schema 2020-12 payload schema per fact
kind under `output.factPayloadSchemas`. The Registry imports and validates the declaration with the image, the run's
node snapshot pins the schemas and their digest, and `ResultManifestVerifier` validates every fact payload against the
pinned schema before acceptance, so a violation ends the attempt as failed with a named, bounded reason and nothing
becomes visible. The Python SDK validates automatically before writing, the golden manifest and payload examples are
shared by platform and SDK, and a new `reference-media-probe` 1.1.0 variant gives Docker-backed end-to-end evidence.
Decisions are in [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (platform); Python 3.12 (SDK, reference Analyst); JSON Schema 2020-12 contracts

**Primary Dependencies**: existing only: `JsonSchema.Net` 8.0.5 (Infrastructure, Contracts tests), `jsonschema` 4.x
(SDK), DbUp not touched

**Storage**: PostgreSQL, no migration unless T020 finds that the completion-outcome reason column blocks the 256-character bounded reason (research.md R4), then one forward-only migration widens it: the declaration `manifest` `jsonb` and the node snapshot JSON carry the schemas

**Testing**: xUnit (Contracts, Integration, EndToEnd, architecture tests), pytest (SDK, reference capability)

**Target Platform**: Linux server containers; Analyst `linux/amd64` OCI image

**Project Type**: polyglot monorepo: platform monolith plus Analyst SDK and capability

**Performance Goals**: validation of a maximum-size result adds at most 10% to acceptance time (SC-006)

**Constraints**: bounded validation time (default 5 s) and memory (only payloads inside the existing per-result facts
size limit, instance depth 32, no retained copies), schema limits 16 KiB, depth 8, 256 nodes, 64 kinds (development
values); payloads stored and returned unchanged; no new member endpoint or Web UI

**Scale/Scope**: one manifest contract release, one verifier step, one SDK check, one reference variant

No NEEDS CLARIFICATION remain.

## Constitution Check

*GATE: passed before Phase 0 and again after Phase 1.*

| Principle | Assessment |
| --- | --- |
| I. Architecture authority | Applies the Result Manifest And Lineage and Capability Declaration narratives; see Architecture Updates |
| II. Source-area ownership | Platform code under `src/platform`, SDK and capability under `src/analysts`, contracts in root `contracts/`; no new `src` child |
| III. API-first | JSON Schema 2020-12 contract is authoritative; no new endpoint; no video bytes involved |
| IV. Evidence | Contract, integration, SDK, and Docker-backed end-to-end tests defined in [quickstart.md](quickstart.md) |
| V. Minimal changes | No migration unless T020 finds that the completion-outcome reason column blocks the 256-character bounded reason (research.md R4), then one forward-only migration widens it, no new dependency, no new table; reference 1.0.0 untouched |
| Environment | Provided by existing `environment-setup` and `environment-verify`; no environment spec needed |

No violations; Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/20261023-010826-fact-payload-schemas/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── payload-schema-subset.md
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
contracts/analysis/analyst-manifest/v1/
├── analyst-manifest.schema.json        # 1.2.0, payloadSchema definition
├── releases/1.2.0.schema.json          # released baseline
└── examples/                           # new valid, invalid, fact-payloads.json

src/platform/
├── SocAlytics.Platform.Domain/Registry/            # CapabilityDeclaration.FactPayloadSchemas, FactPayloadSchemaLimits
├── SocAlytics.Platform.Application/
│   ├── Abstractions/IFactPayloadValidator.cs
│   ├── Analysis/Results/ResultManifestVerifier.cs  # payload step, new reasons
│   ├── Analysis/Results/AnalysisResultOptions.cs   # PayloadValidation:MaxDuration
│   ├── Analysis/WorkflowSnapshotMapper.cs          # pin schemas and digest
│   └── Registry/ResolvedWorkflowDefinition.cs
├── SocAlytics.Platform.Infrastructure/
│   ├── Registry/AnalystManifestValidator.cs        # import, limits, refusal
│   ├── Registry/RegistryWorkflowDefinitionResolver.cs
│   ├── Registry/FactPayloadValidator.cs
│   └── Persistence/Migrations/NNNN_analysis_<description>.sql  # conditional (T020), only if the reason column blocks 256 characters
└── Tests/                                          # Contracts, Integration, EndToEnd (reference 1.1.0)

src/analysts/sdk/                                   # result.py, errors.py, manifest loading, contract copy, tests
src/analysts/capabilities/reference-media-probe/    # analyst-manifest.payload-validated.json, Dockerfile.payload-validated, tests

docs/architecture/, README.md files, AGENTS.md      # status corrections
```

**Structure Decision**: extend existing areas in place: the platform layers keep dependencies inward (Domain limits,
Application port, Infrastructure implementation); `functional areas` stay folders.

## Architecture Updates

- **Narratives**: [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md) (packaging
  convention, result validation, and fact indexing paragraphs),
  [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md) (capability declaration carried in
  the manifest), and [Contracts and Compatibility](../../docs/architecture/contracts-and-compatibility.md) (contract
  release history).
- **Design updates**: applied now to `analysts-models-and-hardware.md`, worded as decided but not implemented (the
  implementation turns it into present tense): the manifest declares fact payload schemas per kind as a restricted,
  self-contained subset of JSON Schema 2020-12 (no format assertions, internal `$defs`/`$ref` only, development
  limits); schemas are pinned by the capability declaration, the profile and workflow bindings through the image
  digest, and by each run's node snapshot; acceptance validates every payload before the accepting transaction, so
  rejection leaves no visible fact; payload failures carry distinct named reasons; the SDK validates automatically
  and always fails the write.
- **Status statements at merge**:
  - `analysts-models-and-hardware.md`, result validation: "capability-specific payload validation remains future
    work" must say that capability-specific payload validation is implemented as development evidence and name what
    stays open (no catalog capability, production limit values, no payload interpretation or quality metrics).
  - `analysts-models-and-hardware.md`, fact indexing: "checks its envelope (not capability-specific payload fields)"
    must say the payloads are checked against the pinned schema when the capability declares one.
  - `analysts-models-and-hardware.md`, the paragraph "Fact payload schemas are decided and not implemented yet" must
    become present tense and name the manifest `1.2.0` contract and the implemented reasons.
  - `analyst-capability-catalog.md`, capability declaration: must state that the declaration includes fact payload
    schemas.
  - `contracts-and-compatibility.md`, release history: "The remaining documented Analyst workflow contracts
    (capability declarations) will follow" must record the manifest `1.2.0` release with payload schemas and keep
    only what is still unimplemented as future.
  - `AGENTS.md`, current state: "with capability-specific payload validation deferred" must describe payload schema
    validation as development evidence; `src/platform/README.md`, `src/analysts/sdk/README.md`, the reference
    capability README (1.1.0 variant), `contracts/README.md` (manifest version `1.2.0`, shared payload examples),
    and `README.md` where they state or imply that fact payloads are unvalidated.
- **Documentation task**: an outcome-based task makes `docs/architecture/` and the listed READMEs describe the implemented
  state, re-reading `docs/architecture/` at implementation time (the rewrites listed here are examples, not the whole
  job), with explicit remaining gaps, validated by `node .github/scripts/check-markdown.mjs`.

## Complexity Tracking

No constitution violations.

# Implementation Plan: Analyst Manager Device Key Rotation

**Branch**: `20261025-184607-manager-key-rotation` | **Date**: 2026-10-25 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261025-184607-manager-key-rotation/spec.md`

## Summary

An Active Analyst Manager replaces its device key without losing its registration
identity. The platform adds the Manager self operation `rotateAnalystManagerKey`
(`POST /api/v1/analyst-managers/self/key-rotation`) that validates, in one request, a
bound client-assertion proof from the registered key and one from the replacement key,
then atomically switches the registration's key under optimistic concurrency, records the
retired fingerprint, revokes the queue credentials, audits, and rate-limits per
registration. The Manager adds a `rotate-key` local-control command and a rotation
coordinator that records a pending rotation in its signed local file before the request,
so every interruption converges on one key without re-registration. Contracts and golden
fixtures live under `contracts/analyst-manager/key-rotation/v1/`. See [research.md](research.md).

## Technical Context

**Language/Version**: C# on .NET 10 (platform, Analyst Manager); SDK pinned by `src/platform/global.json`

**Primary Dependencies**: existing only: ASP.NET Core rate limiter, Dapper, DbUp, jose-based proof validation, PKCS#11 (SoftHSM2) in Manager tests

**Storage**: PostgreSQL: new `analyst_manager_key_rotation` table, `key_rotation_count` column, existing proof replay store; Manager signed local file

**Testing**: xUnit v3 in `Integration.Tests`, `Contracts.Tests`, `Host.Tests` (OpenAPI snapshot), `Architecture.Tests`, `EndToEnd.Tests`, and `SocAlytics.Analysts.Manager.Tests`

**Target Platform**: Linux, Windows, macOS Manager; Linux-containerized platform

**Project Type**: polyglot monorepo: .NET monolith (platform) plus desktop/server Manager

**Performance Goals**: rotation under one minute on a healthy connection (SC-001)

**Constraints**: no grace period for the old key; no key escrow or administrator rotation; development values only for proof lifetime, retry window, and rate limit

**Scale/Scope**: one operation, one table, one local-control command, one contract folder

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Result |
| ----------- | -------- |
| I. Architecture authority | Pass. Implements Machine Authentication and Registration And Stamp Binding of `analyst-manager.md`; Architecture Updates below. |
| II. Source-area ownership | Pass. Platform code in `src/platform`, Manager in `src/analysts/manager`, contracts in `contracts/`; no new `src/` child. |
| III. API-First | Pass. Operation and contract first; no UI business logic; control plane handles metadata only. |
| IV. Evidence over claims | Pass. Integration, contract, Manager, and Docker-backed end-to-end tests; no production-readiness claim. |
| V. Focused changes | Pass. No new dependencies; no Web UI change (fleet read already shows the fingerprint). |
| Environment | Pass. Needs only existing .NET, Docker, SoftHSM2 tooling; no environment spec required; Kiota 1.36.0 and Node.js 24 needed by T023's client refresh are already provided by `.github/actions/environment-setup`. |

Post-design re-check: unchanged, no violations. Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/20261025-184607-manager-key-rotation/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── key-rotation.md
├── checklists/
└── tasks.md             # created by /speckit-tasks
```

### Source Code (repository root)

```text
contracts/analyst-manager/key-rotation/v1/          # schema, fixtures, examples (new)
src/platform/
├── SocAlytics.Platform.Domain/Registry/            # RotateKey on AnalystManagerRegistration
├── SocAlytics.Platform.Application/Registry/
│   ├── KeyRotation/                                # RotateManagerKey handler, options, outcomes (new)
│   ├── Abstractions/                               # IRegistrationStore.SwitchKeyAsync, IKeyRotationStore, proof validator claims check
│   ├── MachineAccess/AuthenticateManagerRequest.cs # rotation-only retired-key retry branch
│   ├── RegistryAuditEvents.cs                      # key-rotation events and detail keys
│   └── RegistryOptions.cs                          # KeyRotation options, KeyRotationPerMinute
├── SocAlytics.Platform.Infrastructure/
│   ├── Registry/                                   # Dapper stores, JoseManagerProofValidator claims
│   └── Persistence/Migrations/0033_registry_analyst_manager_key_rotation.sql
├── SocAlytics.Platform.Api/Registry/               # self endpoint, rate-limit policy, outer-auth refusal audit
└── Tests/                                          # Integration, Contracts, Host (OpenAPI snapshot), Architecture, EndToEnd
src/analysts/manager/
├── SocAlytics.Analysts.Manager.Core/{Registration,LocalState,LocalControl,Cli,Platform}/
└── Tests/SocAlytics.Analysts.Manager.Tests/
src/clients/web/                                    # committed OpenAPI snapshot / Kiota client refresh only
```

**Structure Decision**: Follow the existing Registry functional area inside the platform
layers and the Manager's Core project; add the contract folder beside the other
Analyst Manager contracts. The migration number is the next free one at implementation time.

## Architecture Updates

- **Narratives affected**: `docs/architecture/analyst-manager.md` (Machine Authentication,
  Registration And Stamp Binding, local control commands); `docs/architecture/platform-implementation.md`
  (Registry functional area, API surface, rate limiting, audit events, persistence);
  `docs/architecture/analyst-runtime-and-recovery.md` (credential rotation expectations);
  `docs/architecture/security-and-data-governance.md` (`GOV-CRED-002` and `GOV-CRED-003` development evidence);
  `docs/architecture/contracts-and-compatibility.md` (new contract folder).
- **Design updates applied now**: `analyst-manager.md` Machine Authentication now has a
  paragraph, worded as decided and not yet implemented, describing the rotation protocol:
  two bound client-assertion proofs inside one machine-authenticated request, atomic switch
  with retired-fingerprint memory, no grace period for the old key, the same-proofs retry
  within a window and replacement-key recovery, per-registration rate limit, audit, and the
  Manager's pending-rotation recovery; no administrator rotation, escrow, or rebind. The
  implementation turns that paragraph into present tense as development evidence. The
  existing statement "Routine rotation preserves the Manager identity only after proving
  possession of both the registered old key and the replacement key" is unchanged.
- **Status statements to correct at merge**: the documentation task replaces every
  planned, future-tense, or unimplemented wording about Manager key rotation anywhere in
  `docs/architecture/` (including the `GOV-CRED-002` row, which lists rotation and
  replacement as unresolved) with development-evidence wording, keeping production values,
  key custody, attestation, and scheduled or automatic rotation explicitly unresolved or
  out of scope; it also updates `AGENTS.md` and the READMEs that enumerate current
  evidence (root, platform, Manager, contracts) with the rotation capability.
- **Documentation task requirement**: `/speckit-tasks` MUST include a documentation task
  that makes the narratives above and every status statement the feature affects in
  `docs/architecture/` true for the merged state, re-reading the documents at
  implementation time; the rewrites listed here are examples, not the whole job.

## Complexity Tracking

No constitution violations.

# Implementation Plan: Patched MessagePack Pin for the Platform Restore

**Branch**: `20261016-122938-fix-messagepack-audit-pin` | **Date**: 2026-10-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261016-122938-fix-messagepack-audit-pin/spec.md`

## Summary

Add `<PackageVersion Include="MessagePack" Version="2.5.303" />` to `src/platform/Directory.Packages.props`. Central transitive pinning is already enabled, so the Aspire-supplied MessagePack 2.5.302 is raised to the patched version without touching any project or audit setting.

## Technical Context

**Language/Version**: .NET 10 SDK (`src/platform/global.json`), NuGet central package management.

**Primary Dependencies**: existing Aspire packages; no new project reference.

**Storage**: None.

**Testing**: `dotnet restore`, `dotnet build`, and `dotnet test` of `src/platform/SocAlytics.Platform.slnx`.

**Constraints**: audit and `TreatWarningsAsErrors` stay enabled; one line changes.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/platform/`. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Verified by restore, build, and tests. | Pass |
| V. Focused, minimal changes | One package pin. | Pass |

## Project Structure

```text
specs/20261016-122938-fix-messagepack-audit-pin/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/platform/
└── Directory.Packages.props
```

## Architecture Updates

None. The change is a dependency version pin and alters no design decision or architecture statement.

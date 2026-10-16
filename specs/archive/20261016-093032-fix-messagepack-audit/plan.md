# Implementation Plan: Patched MessagePack Pin for the Platform Restore

**Branch**: `20261016-093032-fix-messagepack-audit` | **Date**: 2026-10-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/archive/20261016-093032-fix-messagepack-audit/spec.md`

## Summary

Add one central `PackageVersion` entry for `MessagePack` 2.5.307 to `src/platform/Directory.Packages.props`. Central transitive pinning, already enabled, raises the transitive version, so NuGet audit no longer reports NU1902. Audit and warnings-as-errors stay as they are.

## Technical Context

**Language/Version**: .NET 10 SDK selected by `src/platform/global.json`.

**Primary Dependencies**: existing Aspire 13.4.6 packages; MessagePack 2.5.307 as a transitive pin only, no project reference.

**Storage**: None.

**Testing**: `dotnet restore`, `dotnet build --no-restore`, and `dotnet test --no-build` of `src/platform/SocAlytics.Platform.slnx`.

**Constraints**: one added line in one product file; audit not disabled or weakened.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes. | Pass |
| II. Source-area ownership | Only `src/platform/`. | Pass |
| III. API-first control plane | Not affected. | Pass |
| IV. Evidence over claims | Restore, build, and tests verify the pin. | Pass |
| V. Focused, minimal changes | One pin line. | Pass |

## Project Structure

```text
specs/archive/20261016-093032-fix-messagepack-audit/
├── spec.md
├── plan.md
├── tasks.md
└── checklists/requirements.md

src/platform/
└── Directory.Packages.props
```

## Architecture Updates

None. The change pins a transitive dependency version and alters no design decision or architecture statement.

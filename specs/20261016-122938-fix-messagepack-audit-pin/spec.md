# Feature Specification: Patched MessagePack Pin for the Platform Restore

**Feature Branch**: `20261016-122938-fix-messagepack-audit-pin`

**Created**: 2026-10-16

**Input**: User description: "Platform restore fails with NU1902 because transitive MessagePack 2.5.302 (via Aspire AppHost and Host.Tests, advisory GHSA-qhrr-8q5h-9q3h) is flagged and TreatWarningsAsErrors turns it into an error. In src/platform/Directory.Packages.props, add a central pin of MessagePack to the lowest patched version available (with CentralPackageTransitivePinningEnabled if needed) so dotnet restore, build and test of src/platform/SocAlytics.Platform.slnx pass again; do not disable NuGet audit or TreatWarningsAsErrors."

**Fix for**: #119 (implementation of `specs/20261015-192843-manager-queue-utilization`)

## Problem

`dotnet restore src/platform/SocAlytics.Platform.slnx` fails with `error NU1902: Warning As Error: Package 'MessagePack' 2.5.302 has a known moderate severity vulnerability (GHSA-qhrr-8q5h-9q3h)` in `SocAlytics.Platform.AppHost.csproj` and `SocAlytics.Platform.Host.Tests.csproj`. MessagePack is not referenced directly; it arrives transitively through the Aspire packages. `src/platform/Directory.Build.props` sets `TreatWarningsAsErrors=true`, so the NuGet audit warning, which appeared when the advisory was published, fails the restore.

Evidence: run 38879935947, job "Phase 5: Convergence / Verify the phase", `dotnet restore` exit 1. The stopped implementation changes no platform package or project file, so every implementation that verifies the platform fails the same way. The advisory lists MessagePack below 2.5.303 as vulnerable; 2.5.303 is the first patched 2.5 version, and `src/platform/Directory.Packages.props` already enables `CentralPackageTransitivePinningEnabled`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The platform restores and builds again (Priority: P1)

A maintainer or the implementation automation restores, builds, and tests the platform solution. The transitive MessagePack dependency resolves to the patched version, so NuGet audit reports no advisory and the restore succeeds with audit and warnings-as-errors still enabled.

**Why this priority**: The restore failure blocks verification and merge of every implementation that touches the platform scope.

**Independent Test**: Run `dotnet restore src/platform/SocAlytics.Platform.slnx`, then build and test; all succeed without NU1902.

**Acceptance Scenarios**:

1. **Given** the central pin, **When** the platform solution is restored, **Then** AppHost and Host.Tests resolve MessagePack 2.5.303 and no NU1902 is reported.
2. **Given** the pin, **When** the platform is built and tested, **Then** build and tests pass as before.
3. **Given** the repository settings, **When** the fix is merged, **Then** NuGet audit and `TreatWarningsAsErrors` are still enabled.

### Edge Cases

- A pin of 2.5.303 must not trigger a downgrade warning (NU1605) for any package that requires a higher MessagePack version; if the restore reports one, the lowest patched version satisfying every requirement is used instead.
- Another advisory on a different package is out of scope.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `src/platform/Directory.Packages.props` MUST contain a central `PackageVersion` pin for `MessagePack` at the lowest patched version, 2.5.303, applied to transitive references through the existing `CentralPackageTransitivePinningEnabled` setting.
- **FR-002**: NuGet audit and `TreatWarningsAsErrors` MUST remain enabled; no `NuGetAudit`, `NoWarn`, or `WarningsNotAsErrors` setting for NU1902 is added.
- **FR-003**: No project file, production code, test, or other configuration changes.

### Key Entities

- **Central transitive pin**: a `PackageVersion` entry that raises the version of a package that only arrives transitively.

## Success Criteria *(mandatory)*

- **SC-001**: `dotnet restore src/platform/SocAlytics.Platform.slnx` succeeds with no NU1902.
- **SC-002**: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` succeeds with no new warnings.
- **SC-003**: `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` and the Host tests pass as before.
- **SC-004**: The diff adds one `PackageVersion` line to `Directory.Packages.props` and changes no audit or warning setting.

## Assumptions

- Version 2.5.303 is the lowest patched version of the 2.5 line per the advisory; it is available on nuget.org. If a later advisory marks it vulnerable, a new fix spec raises the pin.
- Dependencies: the platform package management belongs to [Platform Persistence Foundation](../archive/20261005-130700-platform-persistence-foundation/spec.md); the implementation this fix unblocks is #119 (`specs/20261015-192843-manager-queue-utilization`).

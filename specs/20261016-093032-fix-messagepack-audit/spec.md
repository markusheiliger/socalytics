# Feature Specification: Patched MessagePack Pin for the Platform Restore

**Feature Branch**: `20261016-093032-fix-messagepack-audit`

**Created**: 2026-10-16

**Input**: User description: "dotnet restore of src/platform/SocAlytics.Platform.slnx fails with NU1902 for the transitive package MessagePack 2.5.302 (GHSA-qhrr-8q5h-9q3h) in AppHost and Host.Tests, because TreatWarningsAsErrors is on. Pin a patched MessagePack through central package management in src/platform/Directory.Packages.props, without disabling NuGet audit, and verify restore, build and tests."

**Fix for**: #114 (implementation of `specs/20261015-092159-docker-runtime-adapter`)

## Problem

`dotnet restore src/platform/SocAlytics.Platform.slnx` fails with `NU1902` (moderate severity advisory GHSA-qhrr-8q5h-9q3h, "Nested arrays can blow past buffer length limits") for MessagePack 2.5.302 in `SocAlytics.Platform.AppHost` and `SocAlytics.Platform.Host.Tests`. `src/platform/Directory.Build.props` sets `TreatWarningsAsErrors`, so the audit warning is a restore error. No project under `src/` references MessagePack; it arrives transitively through the Aspire packages listed in `src/platform/Directory.Packages.props`. The advisory affects MessagePack versions below 2.5.303; 2.5.303 is the first patched version.

Evidence: repair verification run 38868748525 of #114 failed at `dotnet restore` with NU1902 in both projects. `Directory.Packages.props` already enables `CentralPackageTransitivePinningEnabled`, so a central `PackageVersion` entry raises the transitive version. Every platform verification, including the final merge of any implementation, fails until the pin is on the default branch.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Platform restore passes with NuGet audit enabled (Priority: P1)

A maintainer or the verification environment restores, builds, and tests the platform solution. The transitive MessagePack resolves to a patched version, so NuGet audit reports no NU1902 and the restore succeeds with audit and warnings-as-errors unchanged.

**Why this priority**: The advisory error blocks verification and merge of every platform implementation.

**Independent Test**: From the repository root run `dotnet restore src/platform/SocAlytics.Platform.slnx`, `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, and `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`; all succeed.

**Acceptance Scenarios**:

1. **Given** the Aspire packages bring MessagePack transitively, **When** the solution is restored, **Then** the resolved MessagePack version is at least 2.5.303 and no NU1902 is reported.
2. **Given** the pin, **When** the solution is built and tested, **Then** no new warning or failure appears and previously passing tests still pass.

### Edge Cases

- The pin must stay on the 2.x line the Aspire packages were built against, so no major-version break occurs.
- The pin must not itself carry a known advisory; restore reports it otherwise.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: `src/platform/Directory.Packages.props` MUST contain a `PackageVersion` entry for `MessagePack` at `2.5.307`, a patched 2.x version, so central transitive pinning resolves every project's transitive MessagePack to it.
- **FR-002**: NuGet audit, `TreatWarningsAsErrors`, and any `NuGetAuditLevel`, `NoWarn`, or `WarningsNotAsErrors` setting MUST remain unchanged; no advisory is suppressed.
- **FR-003**: No other file, project reference, production code, or test changes; no test is skipped, loosened, or removed.

### Key Entities

- **Central transitive pin**: a `PackageVersion` entry that overrides the version of a package that only arrives as a dependency.

## Success Criteria *(mandatory)*

- **SC-001**: `dotnet restore src/platform/SocAlytics.Platform.slnx` exits 0 with no NU1902 or other NU19xx warning.
- **SC-002**: `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` succeeds with 0 warnings and `dotnet test` of the platform solution passes with no regression.
- **SC-003**: The diff changes only the one `PackageVersion` line in `Directory.Packages.props` (FR-002, FR-003).

## Assumptions

- 2.5.307 is chosen as the newest published 2.x release, patched against GHSA-qhrr-8q5h-9q3h (first patched 2.5.303) and compatible with the Aspire dependency range; if restore reports a different advisory for it, the smallest higher patched 2.x version is used instead.
- Dependencies: the feature owning the dependency set is the Aspire local composition of the platform (see `src/platform/Directory.Packages.props`); the implementation this fix unblocks is #114 (`specs/20261015-092159-docker-runtime-adapter`).

---

description: "Task list for Patched MessagePack Pin for the Platform Restore"
---

# Tasks: Patched MessagePack Pin for the Platform Restore

**Input**: Design documents from `specs/20261016-122938-fix-messagepack-audit-pin/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes dependency management; its verification is restore, build, and test.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - The platform restores and builds again (Priority: P1) 🎯 MVP

- [ ] T001 [US1] In `src/platform/Directory.Packages.props`, add `<PackageVersion Include="MessagePack" Version="2.5.303" />` to the `ItemGroup`. Change no audit or warning setting and no other file. Verify: `dotnet restore src/platform/SocAlytics.Platform.slnx` reports no NU1902 or NU1605, `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` succeeds, and `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` passes, and `dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests` passes.

## Dependencies & Execution Order

T001 is the only task.

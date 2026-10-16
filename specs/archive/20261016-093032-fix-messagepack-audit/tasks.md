---

description: "Task list for Patched MessagePack Pin for the Platform Restore"
---

# Tasks: Patched MessagePack Pin for the Platform Restore

**Input**: Design documents from `specs/archive/20261016-093032-fix-messagepack-audit/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md)

**Tests**: The task changes dependency configuration; its verification is restore, build, and the existing tests.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: User Story 1 - Platform restore passes with NuGet audit enabled (Priority: P1) 🎯 MVP

- [X] T001 [US1] In `src/platform/Directory.Packages.props`, add `<PackageVersion Include="MessagePack" Version="2.5.307" />` as the last entry of the existing `PackageVersion` item group (the group has no ordering to preserve). Change nothing else outside this spec folder; do not touch audit or warning settings. Verify: `dotnet restore src/platform/SocAlytics.Platform.slnx` exits 0 with no NU19xx warning, then `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore` has 0 warnings and `dotnet test src/platform/SocAlytics.Platform.slnx --no-build` passes (Docker required, as for every platform test run).

## Dependencies & Execution Order

T001 is the only task.

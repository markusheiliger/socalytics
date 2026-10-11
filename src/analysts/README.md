# Analysts

## Purpose

This area is reserved for the SocAlytics execution-plane Analyst ecosystem across supported runtimes.

## Planned Ownership

The planned boundary includes the future Analyst Manager at `analysts/manager`, the Analyst SDK, and Analyst capability implementations. These components may use different implementation languages while remaining within one capability-focused ownership area.

## Exclusions

Platform APIs, client applications, Hermes, Coach Agents, and Specialist Agents do not belong here. `manager/` contains the executable Analyst Manager (see [manager/README.md](manager/README.md)); no Analyst SDK or capabilities exist yet.

## Current Status

`manager/` contains the executable Analyst Manager desktop app (tray app, Core worker, tests, and macOS Swift bridge source); see [manager/README.md](manager/README.md). No Analyst SDK or capabilities exist yet, and there is no package, runtime, or deployment support.

## Authoritative Architecture

- [Analyst Manager](../../docs/architecture/analyst-manager.md)
- [Analyst runtime and recovery](../../docs/architecture/analyst-runtime-and-recovery.md)
- [Analysts, models, and hardware](../../docs/architecture/analysts-models-and-hardware.md)
- [Analyst capability catalog](../../docs/architecture/analyst-capability-catalog.md)

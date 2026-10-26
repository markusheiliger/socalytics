# Analysts

## Purpose

This area is reserved for the SocAlytics execution-plane Analyst ecosystem across supported runtimes.

## Planned Ownership

The planned boundary includes the future Analyst Manager at `analysts/manager`, the Analyst SDK, and Analyst capability implementations. These components may use different implementation languages while remaining within one capability-focused ownership area.

## Exclusions

Platform APIs, client applications, Hermes, Coach Agents, and Specialist Agents do not belong here. `manager/` contains the executable Analyst Manager (see [manager/README.md](manager/README.md)); `sdk/` contains the executable Python Analyst SDK (its author documentation is `sdk/README.md`); `capabilities/` contains the development reference capability `reference-media-probe` (see [capabilities/reference-media-probe/README.md](capabilities/reference-media-probe/README.md)), the development shot-boundary capability `media-context-classification` (see [capabilities/media-context-classification/README.md](capabilities/media-context-classification/README.md)), and the development evaluation tooling `analyst-evaluation-harness` (`capabilities/analyst-evaluation-harness/`, Python 3.12 and `uv`; it evaluates one Analyst capability against a labelled synthetic corpus and is not a capability); no catalog Analyst capability exists yet.

## Current Status

`manager/` contains the executable Analyst Manager desktop app (tray app, Core worker, tests, and macOS Swift bridge source); see [manager/README.md](manager/README.md). `sdk/` contains the executable Python Analyst SDK (`socalytics_analyst`: contract models, input verification, result writing, and the container entry point, with an example and tests); its author documentation is `sdk/README.md`. A job whose pinned video policy provides media carries a materialized video segment: the Analyst Manager obtains attempt-scoped read grants from the platform, verifies and stages the segment manifest and `segment.mp4` read-only at `/socalytics/media/`, and the SDK exposes them as `context.media`; the Analyst has no network and no credentials, and no transcoding exists. `capabilities/reference-media-probe/` is the first project under `capabilities/` (`capabilities/media-context-classification/` is the second, described below): a development reference Analyst (`reference-media-probe` 1.0.0, built on the SDK) that decodes the staged segment media of a job and writes one `frame-second-probe` fact per match-second, with pytest tests and a digest-pinned `linux/amd64` OCI image that the Docker-backed end-to-end tests run under the Manager's restrictions. It is development evidence only: it is not a catalog capability, has no model, and is never published. `capabilities/media-context-classification/` is a development shot-boundary Analyst (`media-context-classification` 1.0.0, built on the SDK): it decodes the staged segment media of a job with fixed rules and no model and writes one `shot-boundary` fact per detected shot boundary, with pytest tests including a dependency audit, a `linux/amd64` OCI image, and Docker-backed end-to-end evidence in `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests` (commands in the repository [README.md](../../README.md) and its own README). Its detection settings are not calibrated; it does not meet the catalog's quality gates, is not a catalog capability, and is never published. `capabilities/analyst-evaluation-harness/` is the third project under `capabilities/`: development evaluation tooling (`socalytics-analyst-evaluation-harness`, a command-line tool with `evaluate`, `validate-corpus`, and `verify-report`) that runs a subject on a labelled synthetic corpus and writes a schema-validated report with per-profile metrics, gate verdicts, determinism, and lineage. It is not a catalog capability, has no production thresholds, makes no promotion decision, and uses no real footage. Catalog Analyst capabilities remain future, and there is no package publication, production runtime, or deployment support.

## Authoritative Architecture

- [Analyst Manager](../../docs/architecture/analyst-manager.md)
- [Analyst runtime and recovery](../../docs/architecture/analyst-runtime-and-recovery.md)
- [Analysts, models, and hardware](../../docs/architecture/analysts-models-and-hardware.md)
- [Analyst capability catalog](../../docs/architecture/analyst-capability-catalog.md)

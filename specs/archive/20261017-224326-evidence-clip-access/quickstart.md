# Quickstart: Evidence Clip Access

Validation guide for the implemented feature. Commands run from the repository root unless stated otherwise.

## Prerequisites

- .NET 10 SDK (`src/platform/global.json`), a running Docker-compatible runtime, and the pinned FFmpeg and ffprobe of
  the [Analyst and Media Verification Environment](../20261017-163801-analyst-media-environment/spec.md) on `PATH`
  (the Segment Service and the synthetic test media need them; `ffmpeg -encoders` lists `libvpx-vp9`).
- Node.js 24, Kiota 1.36.0, and Playwright 1.63.0 Chromium for the Web UI.
- The features this one depends on are merged: Materialized Video Segments, Accepted Fact Indexing, and Web UI
  Connected Analysis Review.

## Platform

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

Expected: the evidence planner, resolution, authorization, audit, logging, rate-limit, and options tests pass; the
OpenAPI snapshot test passes with `resolveAnalysisEvidenceMedia`; the end-to-end test resolves a fact of the
development workflow over a materialized synthetic recording and downloads media whose digest equals the returned
`mediaDigest`.

After API changes, refresh the snapshot (never in the verification environment):

```powershell
$env:SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT = '1'
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests
```

## Web UI

```powershell
cd src/clients/web
npm ci
npm run build
npm test
npm run test:e2e -- --project simulated
npm run test:e2e -- --project platform
```

Expected: unit tests cover the adapter and the playback state machine; simulated tests cover every player state
([evidence-player.md](contracts/evidence-player.md)), piece sequencing, expiry renewal, the security policy and CORS
([media-origin-and-storage.md](contracts/media-origin-and-storage.md)), keyboard, axe, offline, and absence of stored
data; the platform test plays the evidence of a fact of an uploaded synthetic recording.

## Manual check

1. `dotnet run --project src/platform/SocAlytics.Platform.AppHost`, sign in as a Coach, upload and finalize a short
   recording, and wait for its run to complete.
2. Open the run's analysis review, choose a capability, and choose "Play evidence" on a fact: the dialog plays 5
   seconds before the fact, the fact, and 5 seconds after it.
3. As a Club Admin, purge the match's materialized segments
   ([member operations](../20261017-222433-materialized-segments/contracts/member-operations.md)) and play the evidence
   again: the dialog reports that the video is being prepared and plays once it is available.
4. Check that the API logs and the security audit trail contain no presigned link.

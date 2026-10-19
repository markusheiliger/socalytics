# Implementation Plan: Evidence Clip Access

**Branch**: `20261017-224326-evidence-clip-access` | **Date**: 2026-10-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261017-224326-evidence-clip-access/spec.md`

## Summary

Add the member operation `resolveAnalysisEvidenceMedia` (`POST /api/v1/analysis/runs/{runId}/evidence-media`) that
resolves one indexed fact (with bounded context) or an explicit bounded range of a run to ordered, contiguous parts
derived from the run's own recording-set lineage and pinned segmentation policy. Available parts carry a short-lived
presigned `GET` link to the covering materialized segment's media, its digest, and playback offsets computed from the
segment manifest; other parts report `materializing`, `rematerializing` (after the operation requested the segment
from the Segment Service), `failed`, or `not-covered`. Authorization is evaluated per request, links are never stored
or logged, resolutions that issue links are audited, and a per-member rate limit applies. The Web UI analysis review
gains a "Play evidence" dialog that plays the parts directly from object storage across segment boundaries, with
explicit states and link renewal; the Content Security Policy allows media from the object-store origin only
(research R1–R13).

## Technical Context

**Language/Version**: C# on .NET 10 (platform); TypeScript with React (Web UI, Node.js 24).

**Primary Dependencies**: Existing only: ASP.NET Core minimal APIs and rate limiting, Npgsql and Dapper, the AWS S3 SDK
adapter (`S3ObjectStorage`), the JSON Schema validator used for contracts, xUnit v3, Testcontainers; Web UI React, MUI,
TanStack Query, i18next, the generated Kiota client, Vitest, Playwright 1.63.0 Chromium, axe. External executables in
tests only: the pinned `ffmpeg` and `ffprobe` (environment spec), for synthetic media.

**Storage**: No new table or migration. Reads: analysis runs, workflow snapshots, indexed facts (Accepted Fact
Indexing), recording-set lineage, materialized-segment records (Materialized Video Segments). Writes: materialization
demand through the Segment Service and security audit events. Object storage: checksum attestation and manifest reads
(at most 64 KiB) plus presigned `GET` links; no media bytes are read.

**Testing**: Domain tests for the coverage planner; Integration tests with PostgreSQL, RustFS, and the toolchain;
Architecture tests (endpoint security); Host tests (OpenAPI snapshot, options); an EndToEnd test over the development
workflow; Web UI Vitest unit tests and Playwright `simulated` and `platform` projects.

**Target Platform**: The API host on developer machines and the verification environment; Chromium-based browsers for
the Web UI.

**Project Type**: Additions to the existing web service (platform) and web client (Web UI).

**Performance Goals**: One resolution performs at most one checksum attestation and one manifest read per distinct
segment and no media transfer; development evidence only, no production throughput target.

**Constraints**: The API never reads or relays video bytes; links are per-object, read-only, short-lived, never stored
or logged; authorization on every request; media only from the object-store origin; online only; production link
lifetimes, rate limits, and CDN delivery remain unresolved.

**Scale/Scope**: One HTTP operation, one options section, one rate-limiter policy, one audit event type, one Web UI
dialog with an adapter and a state machine, CSP and fake-store changes.

## Constitution Check

| Gate | Before research | After design |
| --- | --- | --- |
| I. Architecture authority | Pass: implements the API side of `get_video_clip()`, the review's evidence playback, and artifact-authorization audit as the architecture names them | Pass: design decisions applied to the owning narratives as decided, not yet implemented (see Architecture Updates) |
| II. Source-area ownership | Pass: platform code in `src/platform` layers, Web UI code in `src/clients/web` | Pass: no new project or first-level area; the `Evidence` folders appear with their first artifacts |
| III. API-first control plane | Pass: the Web UI uses an API operation; the API issues presigned access and never proxies video | Pass: only metadata and the manifest document are read; media flows between object storage and the browser; the member operation is described in the OpenAPI document |
| IV. Evidence over claims | Pass: domain, integration, host, end-to-end, unit, and browser tests required | Pass: development values only; production values stay explicitly unresolved |
| V. Focused changes | Pass: no MCP tool, downloads, sharing, encodes, or whole-recording playback | Pass: no table, no new contract schema, no new dependency |
| Environment prerequisite | Pass: [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (#143) provides the pinned FFmpeg and ffprobe (LGPL with libvpx); .NET, Docker, Node.js, Kiota, and Playwright Chromium are already provided | Pass: no other framework, SDK, or tool is needed; R12 records the `libvpx-vp9` check |

No violation; Complexity Tracking is empty.

## Architecture Updates

**Narratives**:

- [Analysts, Models, and Hardware → Capability-Scoped Result Access](../../docs/architecture/analysts-models-and-hardware.md#capability-scoped-result-access)
  (member evidence-media operation).
- [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md) (the `get_video_clip()` sentence
  after the catalog table).
- [Intelligence and Agents → MCP Tools](../../docs/architecture/intelligence-and-agents.md#mcp-tools)
  (`get_video_clip()` bullet).
- [Client Applications → Web UI](../../docs/architecture/client-applications.md#web-ui) (analysis review and evidence
  playback).
- [Match Data Pipeline → Segment Contract](../../docs/architecture/match-data-pipeline.md#segment-contract) (the
  Segment Service paragraph: evidence demand and purge).
- [Terminology and Principles → 4. Control Plane Never Touches Video](../../docs/architecture/terminology-and-principles.md#4-control-plane-never-touches-video).
- [Security and Data Governance → Audit Events](../../docs/architecture/security-and-data-governance.md#audit-events)
  and the Web UI security baseline in
  [Accepted Web UI Offline Local-Data Boundary](../../docs/architecture/security-and-data-governance.md#accepted-web-ui-offline-local-data-boundary)
  (Content Security Policy).

**Design updates applied now** (worded as decided, not yet implemented; the documentation task turns them into present
tense):

- `analysts-models-and-hardware.md` (Capability-Scoped Result Access): new paragraph on the member evidence-media
  operation (fact or bounded range, run-only lineage, first recording in set order, part states, integrity check,
  links never stored or logged, per-request authorization, rate limit, audit), the API side of `get_video_clip()`.
- `analyst-capability-catalog.md`: the API side of `get_video_clip()` is that operation; the MCP tool stays future.
- `intelligence-and-agents.md` (MCP Tools): the `get_video_clip()` bullet names its API side as decided.
- `client-applications.md` (Web UI): the review "plays no video other than the evidence playback below"; new
  paragraph on evidence playback.
- `match-data-pipeline.md` (Segment Contract, Segment Service paragraph): evidence resolutions request `absent`
  segments without pinning them against the purge.
- `terminology-and-principles.md` (section 4): browsers read evidence media from object storage; the diagram shows
  client video bytes in both directions.
- `security-and-data-governance.md` (Audit Events): artifact authorization includes member evidence-media links,
  recorded without the links; (Web UI baseline): `media-src` becomes `blob: <object-storage-origin>` with CORS-mode
  media loading.

**Status statements at merge** (each must become true for the merged state):

- `analysts-models-and-hardware.md`, Capability-Scoped Result Access: "The evidence of those facts is decided, but not
  yet implemented, to be resolvable to video" → present tense; "the MCP tool remains future work" stays.
- `analyst-capability-catalog.md`: "The API side is decided, but not yet implemented, as the member evidence-media
  operation" → implemented; the MCP tool remains future work.
- `intelligence-and-agents.md`, MCP Tools: "(its API side … is decided, not yet implemented; the MCP tool is future
  work)" → the API side is implemented.
- `client-applications.md`, Web UI: "Evidence playback in the analysis review is decided as follows and not yet
  implemented" → present tense; and the bullet "team and match review, analytics, and agent conversations for coaches
  (analysis review and agent conversations are not implemented yet …)" must state the review's and the playback's
  actual status at merge.
- `match-data-pipeline.md`, Segment Contract: "A member's evidence resolution … is decided, not yet implemented, to
  request the materialization" → present tense.
- `terminology-and-principles.md`, section 4: "Browsers are decided, but not yet implemented, to also read evidence
  media" → present tense.
- `security-and-data-governance.md`, Audit Events: "(including member evidence-media links, recorded without the
  links; decided, not yet implemented)" → implemented; Web UI baseline: "`media-src` is decided, but not yet
  implemented, to become `blob: <object-storage-origin>`" → the policy quote itself lists `media-src blob:
  <object-storage-origin>`, and "`media-src blob:` only lets a detached video element read a local file's duration"
  names both purposes.
- `intelligence-and-agents.md`, "Agents access only accepted facts, concepts, evidence timestamps, and authorized clip
  references through MCP/API tools": stays true (agents remain future); check no text claims agents already use clips.
- Any other statement found at implementation time that says members cannot play video, that evidence clips are not
  available through the API, or that the Web UI plays no media from object storage.

The documentation task MUST make the listed narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time; the rewrites above are examples, not the whole
job. It also updates `AGENTS.md` (Current State: the evidence operation and playback, the `Analysis:Evidence` options),
`src/platform/README.md` (operation, options, rate limit, audit event), and `src/clients/README.md` (evidence playback,
test media prerequisite).

## Project Structure

### Documentation (this feature)

```text
specs/20261017-224326-evidence-clip-access/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── evidence-media-api.md
│   ├── evidence-player.md
│   └── media-origin-and-storage.md
├── checklists/requirements.md
└── tasks.md                 # /speckit-tasks
```

### Source Code (repository root)

```text
src/platform/
├── SocAlytics.Platform.Domain/Analysis/Evidence/
│   ├── EvidenceCoveragePlanner.cs                     # parts from range, ordered members, spans, policy duration
│   └── EvidencePartState.cs
├── SocAlytics.Platform.Application/
│   ├── Analysis/Evidence/
│   │   ├── EvidenceOptions.cs                         # Analysis:Evidence, validated at start
│   │   ├── ResolveEvidenceMedia.cs                    # handler: authorize, plan, segment states, demand, checks, links, audit
│   │   ├── SegmentManifestOffsets.cs                  # offsets from manifest spans and encoded interval
│   │   └── EvidenceAuditEvents.cs                     # analysis.evidence-media.granted
│   ├── Analysis/Facts/IFactQueries.cs                 # + GetRunFactAsync
│   └── ApplicationServiceCollectionExtensions.cs      # registrations
├── SocAlytics.Platform.Infrastructure/Persistence/Analysis/FactQueries.cs   # GetRunFactAsync
├── SocAlytics.Platform.Api/
│   ├── Analysis/AnalysisEvidenceEndpoints.cs          # resolveAnalysisEvidenceMedia
│   ├── Analysis/EvidenceRateLimiting.cs               # analysis-evidence-media policy
│   ├── Program.cs                                     # mapping and rate-limiter registration
│   └── openapi/v1.json                                # refreshed snapshot
├── SocAlytics.Platform.AppHost/Program.cs             # Analysis__Evidence__* development values
└── Tests/
    ├── SocAlytics.Platform.Integration.Tests/
    │   ├── Analysis/Evidence/                         # planner, resolution states, demand, integrity, offsets,
    │   │                                              # authorization, audit, logs, rate limit, options
    │   └── IdentityAccess/Support/TestIdentityAccessSettings.cs   # Analysis:Evidence values
    ├── SocAlytics.Platform.Architecture.Tests/EndpointSecurityTests.cs   # settings and endpoint rules
    ├── SocAlytics.Platform.Host.Tests/                # OpenAPI snapshot, MinimumWebVersionTests settings
    └── SocAlytics.Platform.EndToEnd.Tests/EvidenceMediaEndToEndTests.cs

src/clients/web/
├── vite.config.ts                                     # media-src with the object-store origin
├── src/
│   ├── i18n/en/analysis.json                          # evidence.* keys
│   ├── platform/models/analysis.ts                    # evidence resolution, parts, media pieces
│   ├── platform/adapters/analysisEvidenceAdapter.ts   # resolve
│   └── features/analysis/
│       ├── FactBrowser.tsx                            # "Play evidence" per fact
│       ├── EvidencePlayerDialog.tsx                   # dialog, two alternating video elements, live region
│       ├── useEvidencePlayback.ts                     # state machine, re-resolution, renewal
│       └── evidenceTimeline.ts                        # piece order, offsets, inside-fact, expiry margins
└── tests/
    ├── unit/platform/adapters/analysisEvidenceAdapter.test.ts
    ├── unit/features/analysis/useEvidencePlayback.test.ts, evidenceTimeline.test.ts, EvidencePlayerDialog.test.tsx
    ├── unit/serviceWorker/shellWorker.test.ts         # object-store media stays unhandled
    └── e2e/
        ├── support/fakeObjectStorage.mjs, fakeObjectStorage.d.mts   # seeded GET/HEAD with Range and CORS
        ├── support/syntheticVideo.ts                  # VP9 MP4 through the pinned ffmpeg
        ├── support/simulatedApi.ts                    # evidence-media route
        ├── simulated/headers.spec.ts                  # new policy
        ├── simulated/evidence-playback.spec.ts        # states, sequencing, renewal, CORS, axe, keyboard, offline, storage
        └── platform/evidence-playback.spec.ts         # plays a fact of an uploaded synthetic recording
```

**Structure Decision**: The `Evidence` functional folder is added inside the existing Domain and Application `Analysis`
area beside `Facts`, and the endpoint beside the other analysis endpoints in the Api layer; the player joins the
review's `features/analysis` folder over a new adapter. No project, table, or contract schema is added.

## Complexity Tracking

No violations.

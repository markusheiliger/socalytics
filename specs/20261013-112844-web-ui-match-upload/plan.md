# Implementation Plan: Web UI Match Workflow and Recording Upload

**Branch**: `20261013-112844-web-ui-match-upload` | **Date**: 2026-10-13 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/20261013-112844-web-ui-match-upload/spec.md`

## Summary

Extend the Web UI at `src/clients/web/` (Web UI Foundation) with the path from a
team's matches to a finalized recording set, using only existing platform
operations.

- **Matches**: team match lists, match creation and editing for Club Admins and
  the team's Coaches, read-only for Viewers, with `If-Match` conflicts as in the
  foundation.
- **Upload**: a dedicated upload worker hashes each video file into 16 MiB
  parts with SHA-256, transfers the parts directly to object storage with the
  platform's presigned per-part grants (four parts in parallel, one file at a
  time per browser), retries with back-off, renews grants, and keeps a
  member-scoped resume journal in IndexedDB that never holds grants or file
  contents. The main-thread `UploadCoordinator` makes every platform call
  through new adapters with idempotency keys reused per intent.
- **Recovery**: after a reload, the member reselects the file; the worker
  verifies its composite digest against the platform's session and sends only
  the missing parts. Explicit sign-out purges the member's journal.
- **Timeline mapping and finalization**: the duration is read from the local
  file to propose one span; members finalize ordered selections, with a
  confirmation for a repeat of the newest set.
- **Lineage and analysis runs**: read-only lineage and analysis run states,
  refreshed while runs are unfinished.
- **Platform touch points**: the AppHost allows the Web UI origin on the
  development RustFS and passes the storage origin to the Web UI, whose
  Content Security Policy adds it to `connect-src`, plus `media-src blob:` and a
  Trusted Types policy for the worker.

Design rationale: [research.md](research.md). Client models:
[data-model.md](data-model.md). Contracts: [contracts/](contracts/).
Validation: [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: TypeScript 5.x (strict) on Node.js 24, as in the
foundation; C# on .NET 10 for the AppHost change.

**Primary Dependencies**: the foundation's pinned dependencies (React 19, React
Router 7, TanStack Query 5, MUI 7, i18next, `@microsoft/kiota-bundle`, Vite 7,
Vitest, Testing Library, `@playwright/test` 1.63.0, `@axe-core/playwright`).
New: `fake-indexeddb` (exact version, dev dependency) for journal unit tests.
Browser APIs: Web Workers, `crypto.subtle`, `XMLHttpRequest` upload progress,
IndexedDB, Web Locks, and `HTMLVideoElement` metadata. No new production
dependency.

**Storage**: the platform remains the system of record. The browser keeps only
the resume journal (IndexedDB `socalytics-upload-journal`, worker-owned,
member-scoped, purged on explicit sign-out; [data-model.md](data-model.md#journal-record)).

**Testing**:

- `npm test` (Vitest): part policy, digest vectors, retry, grant scheduling,
  coordinator state machine, refusal mapping, journal with `fake-indexeddb`,
  sign-out purge, mapping conversion, worker policy, duplicate-set detection.
- `npm run test:e2e`: `simulated` project with the routed API and a Node fake
  object store (`tests/e2e/support/fakeObjectStorage.mjs`) as a second
  `webServer`; `platform` project against the AppHost `e2e` mode with RustFS.
- `dotnet test` Host tests for the AppHost settings.

**Target Platform**: Chromium-based browsers (automated: Chromium on Linux).

**Project Type**: Web application (single-page application) consuming the
existing platform API.

**Performance Goals**:

- SC-001: a Coach goes from a team to a finalized two-recording set in under 5
  minutes of interaction.
- SC-006, quantified: while a 1 GiB file is prepared and transferred, no
  main-thread long task exceeds 200 ms and navigation works.
- Transfers use four parallel parts of 16 MiB.

**Constraints**:

- No recording bytes to the platform API (FR-007).
- No grants or secrets in browser storage (FR-014).
- Idempotency keys reused per intent (FR-013).
- Video files only (FR-005).
- The CSP stays strict except for the listed additions (FR-026).
- English through translation keys and WCAG 2.2 AA (FR-028).

**Scale/Scope**: About 6 new routes and views, an upload indicator, one worker,
4 adapters, 5 user stories.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | Implements `client-applications.md` § Web UI (match creation, recording uploads, upload finalization) and `match-data-pipeline.md` § Upload (direct multipart upload with per-part grants, composite digest, the API never reads media). Stays online-only; uploads stay connected-only as § Offline Mode In A PWA requires. The resume journal is not the offline replica; this change records it in `client-applications.md` § Web UI (worker-owned, minimal, purged on sign-out). It also records the development CORS and storage origin in `platform-implementation.md` § Object Storage, and the CSP changes in the Web UI security baseline of `security-and-data-governance.md`. Production CORS, origins, and lifetimes stay unresolved. | Pass |
| II. Source-area ownership | Web UI code only under `src/clients/web/`; the AppHost change stays in `src/platform/SocAlytics.Platform.AppHost/` with its Host test. | Pass |
| III. API-first control plane | The Web UI uses only existing operations through the generated client and adapters; media bytes go only to object storage through the S3 grants the platform issues; the API never relays video. | Pass |
| IV. Evidence over claims | Unit tests, simulated browser tests for every scenario, the `platform` end-to-end set with RustFS, axe and CSP checks, and the Host test; no production readiness claimed. | Pass |
| V. Focused, minimal changes | No platform API change and no new production dependency; one dev dependency (`fake-indexeddb`) for journal tests. The `PutBucketCors` fallback is added only if the RustFS variable proves insufficient. | Pass |
| Tooling constraints | The merged environment feature `specs/archive/20261012-145505-web-ui-environment` provides everything the tasks need: Node.js, Kiota 1.36.0, Playwright 1.63.0 Chromium, the Web UI check (`build`, `test`, `test:e2e`), the .NET SDK, and Docker for the `platform` project. The fake object store is a Node script inside the project. | Pass |

Post-design re-check: passes; no Complexity Tracking entries.

## Project Structure

### Documentation (this feature)

```text
specs/20261013-112844-web-ui-match-upload/
├── plan.md
├── research.md          # Decisions R1–R14
├── data-model.md        # Client models, upload job states, journal record
├── quickstart.md
├── contracts/
│   ├── routes-and-roles.md       # New routes, roles, shell additions
│   ├── platform-adapter.md       # Match, upload, lineage, analysis-run adapters; error mapping
│   ├── upload-worker.md          # Worker and coordinator message protocol
│   ├── security-headers.md       # CSP changes: storage origin, media-src blob:, worker policy
│   └── development-storage.md    # AppHost CORS and storage origin; fake object store
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/clients/web/
├── package.json, package-lock.json              # + fake-indexeddb (dev, exact)
├── vite.config.ts                               # CSP additions from SOCALYTICS_OBJECT_STORAGE_ORIGIN
├── playwright.config.ts                         # simulated: fake object store webServer and its origin
├── src/
│   ├── platform/                                # + matchAdapter, recordingUploadAdapter, recordingLineageAdapter, analysisRunAdapter, error mapping additions
│   ├── uploads/
│   │   ├── partPolicy.ts, digests.ts, retryPolicy.ts, grantScheduler.ts
│   │   ├── uploadCoordinator.ts, uploadRepository.ts, workerPolicy.ts, duration.ts
│   │   └── worker/uploadWorker.ts, journal.ts, transfer.ts, hashing.ts
│   ├── shell/                                   # + upload indicator and panel, beforeunload, sign-out confirmation and purge
│   ├── i18n/en/matches.json, uploads.json
│   └── features/
│       ├── teams/                               # + team match list
│       ├── matches/                             # create, edit, match view
│       ├── uploads/                             # add files, queue, progress, resume, abandon, mapping editor
│       └── lineage/                             # lineage, recording version, revise mapping, finalize, analysis run states
├── tests/unit/uploads/, tests/unit/platform/, tests/unit/features/
└── tests/e2e/
    ├── simulated/matches.spec.ts, uploads.spec.ts, recovery.spec.ts, finalize.spec.ts, lineage.spec.ts, headers.spec.ts
    ├── platform/upload-flow.spec.ts
    └── support/fakeObjectStorage.mjs, simulatedApi.ts (+ match and recording fixtures), testFiles.ts

src/platform/
├── SocAlytics.Platform.AppHost/Program.cs                         # RUSTFS_CORS_ALLOWED_ORIGINS, SOCALYTICS_OBJECT_STORAGE_ORIGIN
└── Tests/SocAlytics.Platform.Host.Tests/PlatformHostTests.cs      # asserts both settings

docs/architecture/client-applications.md                # Web UI: upload worker and resume journal
docs/architecture/platform-implementation.md            # Object Storage: development CORS and storage origin
docs/architecture/security-and-data-governance.md       # Web UI security baseline: CSP additions
src/clients/README.md, AGENTS.md                        # Current state
```

**Structure Decision**: Extend the one npm project `src/clients/web/` from the
foundation. The upload engine lives in `src/uploads/` with its worker, and
features use it only through the coordinator and the repository. The only
platform change is the AppHost composition and its Host test; the OpenAPI
snapshot is unchanged.

## Complexity Tracking

No constitution violations.

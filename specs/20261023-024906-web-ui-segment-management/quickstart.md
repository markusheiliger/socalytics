# Quickstart: Validate Web UI Segment Management

## Prerequisites

- Node.js 24, Kiota 1.36.0 on `PATH`, Playwright 1.63.0 Chromium (see `AGENTS.md`).
- For the `platform` project: a Docker-compatible runtime and the pinned `ffmpeg` and `ffprobe` on `PATH`.

## 1. Unit tests

```powershell
cd src/clients/web
npm ci
npm run build
npm test
```

Expected: adapter, summary, section, and dialog tests pass, including Club Admin only, loading, empty, error,
not-permitted, offline, refresh keeps the previous list, and purge confirm, cancel, success, repeat (zero purged), and
failure paths.

## 2. Simulated browser tests (no Docker)

```powershell
npx playwright test --project simulated tests/e2e/simulated/segments.spec.ts
```

Expected: a Club Admin sees every state (absent, materializing, available, failed) with size, failure reason, and in-use
markers and the summary counts; Coach, Viewer, and Club Admins of another club see no area and no request reaches the
scripted segment endpoints; purge cancel sends nothing; confirm shows purged and in-use counts and the re-read list;
`403`, `503`, offline, and network error states are distinct; no segment data appears in `localStorage`, IndexedDB, or
service-worker caches; axe finds no violations.

## 3. Platform browser project

```powershell
npx playwright test --project platform-segments
```

Expected (the chained projects before it run first): after finalizing an uploaded recording set the area shows real
`failed` and `available` segments, purge returns the unused ones to `absent` and reports the counts, a repeated purge
reports zero, and the area stays online-only.

## 4. Confirm nothing platform-side changed

```powershell
git diff --stat -- contracts src/clients/web/openapi src/platform/SocAlytics.Platform.Api
```

Expected: no change (SC-007). Then run `node .github/scripts/check-markdown.mjs` for the documentation task.

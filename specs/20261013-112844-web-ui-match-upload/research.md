# Research: Web UI Match Workflow and Recording Upload

All decisions build on Web UI Foundation
([plan](../archive/20261012-150657-web-ui-foundation/plan.md),
[research](../archive/20261012-150657-web-ui-foundation/research.md)): Kiota client
behind adapters, in-memory session with `X-CSRF-Token`, TanStack Query, the
`simulated` and `platform` Playwright projects, and the AppHost `e2e` mode.
This feature adds no platform API.

## R1. One upload worker owns hashing, transfers, and the resume journal

- **Decision**: A dedicated module worker (`src/uploads/worker/uploadWorker.ts`)
  prepares files, transfers parts to object storage, and owns the resume
  journal (R6). The main thread's `UploadCoordinator` makes every platform
  call through the adapters (start, grants, completion, session reads) and
  passes grants to the worker by message. Screens use the coordinator and the
  `UploadRepository`; they never touch storage APIs or the worker directly.
- **Rationale**: Hashing gigabytes and tracking byte progress stay off the UI
  thread (SC-006). Keeping platform calls on the main thread keeps the CSRF
  token and the session middlewares of the foundation in one place. The
  structure follows `client-applications.md`: local data owned by a worker,
  reached only through application-owned repositories.
- **Alternatives considered**:
  - Everything on the main thread: `crypto.subtle.digest` is asynchronous, but
    reading and progress bookkeeping for many parts still competes with
    rendering, and the journal would be reachable from UI code.
  - Platform calls from the worker: it would duplicate the Kiota pipeline,
    the anti-forgery token, and session-end handling.

## R2. Integrity declaration and part size

- **Decision**:
  - Part size: 16 MiB, raised to the next whole MiB that keeps the part count
    at 10 000 or below. A file of up to 16 MiB is one part whose part size
    equals the file size.
  - Each part's digest is `crypto.subtle.digest('SHA-256', bytes)`, read with
    `Blob.slice(start, end).arrayBuffer()` one part at a time and declared as
    `sha-256:<64 lowercase hex>`, the format `StartUploadRequest.partDigests`
    expects.
  - The worker also computes the composite digest exactly as
    `CompositeContentDigest.FromPartDigests` does: SHA-256 over the
    concatenated raw part digests, written as
    `sha-256-parts:<partSize>:<partCount>:<hex>`. Unit tests use the platform's
    known vectors from `CompositeDigestTests`.
- **Rationale**: 16 MiB parts keep one part's memory small, give fine-grained
  resume, and make multi-part browser tests possible with files of a few
  tens of MiB. The composite digest lets the Web UI verify a reselected file
  against the platform's session (R7) and detect duplicates without
  uploading.
- **Platform limits**: storage minimum 5 MiB for non-final parts, at most
  10 000 parts, and the platform's maximum object size. The platform's
  refusal (`upload-declaration-invalid`, `413`, `415`) stays authoritative.
- **Alternatives considered**: 64 MiB parts (coarser resume, heavier tests);
  a streaming SHA-256 library over the whole file (not needed, because the
  platform declares per-part digests).

## R3. Transfers

- **Decision**:
  - Each part is sent by `XMLHttpRequest` `PUT` from the worker to the grant's
    URL. The body is a `Blob.slice(start, end, '')` with an empty type, so no
    `Content-Type` is added. The only header set is `x-amz-checksum-sha256`
    from `requiredHeaders`; the browser derives `Content-Length` from the body,
    which equals the signed length.
  - Four parts of the current file transfer in parallel. Byte progress comes
    from `xhr.upload.onprogress`.
  - A part counts as confirmed only on a `2xx` response.
  - One file transfers at a time per browser profile, through the Web Lock
    `socalytics-upload-slot`. Each upload also holds
    `socalytics-upload:<uploadSessionId>`, so a second tab shows it as running
    elsewhere (edge case "Match opened in two tabs").
- **Rationale**: `fetch` has no upload progress. Web Locks work across tabs
  and in workers in Chromium.
- **Alternatives considered**: `fetch` with streamed bodies (no progress,
  requires HTTP/2 for streaming uploads); `BroadcastChannel` leader election
  (more code than locks).

## R4. Grants

- **Decision**:
  - The coordinator requests grants just in time, for at most 32 parts ahead
    of the transfer position.
  - It renews grants that expire within 60 seconds, and re-grants a part once
    when storage refuses it with `403` (expired or invalid signature) before
    counting a failure.
  - Grants live only in worker and coordinator memory, never in the journal
    (FR-014, SC-007).
- **Rationale**: Development grants live 15 minutes, and a large upload takes
  longer. The platform's `MaxGrantsPerRequest` (1 000 in development) is
  never approached.

## R5. Retry policy

- **Decision**:
  - **Retryable**: network errors, `408`, `429`, and `5xx`. These are retried
    up to 5 times after 1, 2, 4, 8, and 16 seconds (±20 % jitter). After that
    the file is paused with a retry action (FR-009) and yields the transfer
    slot to the next queued file.
  - **`400` with `BadDigest` or a size mismatch** means the file changed on
    disk: the upload is shown as failed and the member is asked to start a new
    upload (edge case).
  - **Unreachable**: while the connectivity provider of the foundation reports
    the platform as unreachable, or while storage is unreachable, transfers
    pause and resume on recovery without counting attempts (FR-016).

## R6. Resume journal

- **Decision**:
  - IndexedDB database `socalytics-upload-journal`, owned by the worker, with
    one object store `uploads` keyed by `[memberId, uploadSessionId]` and an
    index on `[memberId, matchId]`. The fields are in
    [data-model.md](data-model.md#journal-record).
  - **Never stored**: no grants, secrets, or file contents.
  - **Start key**: the start request's idempotency key is written before the
    request is sent, so a reload during start replays the same key and gets
    the original session.
  - **Purge**: explicit sign-out sends `purgeMember` to the worker before
    `DELETE /api/v1/session`, which aborts its transfers and deletes all of
    the member's records (clarification). A session ended by the platform
    keeps the records. Records of completed, abandoned, and acknowledged
    expired uploads are deleted at once.
- **Rationale**: Resume after reload or restart needs persistence (Story 4).
  The record holds only what the platform already knows plus local
  progress.
- **Architecture**: `client-applications.md` rejects UI-thread IndexedDB for
  the offline replica. This journal is not a replica: it holds no member,
  match, or analytics data beyond identifiers and progress, and it is not an
  offline capability, since uploads stay connected-only. It is worker-owned and
  reached only through a repository. The plan records this in
  `client-applications.md` § Web UI.
- **Alternatives considered**:
  - `sessionStorage`: lost on restart.
  - A platform list of pending sessions: it is out of scope, and it would
    change the API.

## R7. Resume and completion refusals

- **Decision**:
  - **On resume**: the coordinator reads the upload session. Completed,
    expired, or forbidden sessions are shown as such. The member reselects
    the file; a size different from the declaration is refused at once.
    Otherwise the worker rehashes with the recorded part size and compares the
    composite digest with the session's `contentDigest`. On a match it
    transfers only the unconfirmed parts; on a mismatch it refuses with a
    clear message and changes nothing (FR-015).
  - **Completion refusals**:
    - `upload-parts-incomplete` or `upload-part-mismatch`: the platform does not
      name the parts, so the coordinator marks every unconfirmed part for
      transfer. If all parts were confirmed, it marks all parts, because the
      local record was stale. It then offers transfer and retry (FR-011,
      Story 2 scenario 6).
    - `upload-object-mismatch` and `integrity-evidence-unavailable`: shown as
      failed with the reason.
    - `upload-session-expired`: shown as expired.
    - `upload-session-completed` on a replay: treated as success, by reading
      the lineage.

## R8. Idempotency keys

- **Decision**: Every start, completion, mapping revision, and finalization
  sends `Idempotency-Key` with a `crypto.randomUUID()` generated once per
  member intent and reused for every retry of that intent (FR-013).
  - **Start and completion**: their keys are kept in the journal, so they
    survive a reload.
  - **Finalization and mapping revision**: their keys live with the open
    dialog. `idempotency-key-reused` is shown as a programming error (unit
    tested).

## R9. Recording duration

- **Decision**: The main thread creates a detached
  `<video preload="metadata" muted>` element with a `blob:` URL of the file.
  It reads `duration` on `loadedmetadata` and revokes the URL. The element is
  never attached to the document and never played.
  - **When no duration is proposed**: when no metadata arrives within 10
    seconds, or the duration is not finite.
  - **CSP**: the policy adds `media-src blob:` (contracts/security-headers.md).
- **Rationale**: The browser's own demuxers cover every format the browser
  identifies as video without a new dependency, and nothing is shown or
  played (FR-024).
- **Alternatives considered**: parsing ISO BMFF `mvhd` in JavaScript (a new
  parser, limited to MP4 and QuickTime).

## R10. File selection

- **Decision**:
  - The picker and drop zone use `accept="video/*"`, and the coordinator
    refuses any `File` whose `type` does not start with `video/`, including an
    empty type (clarification).
  - The declared content type is `file.type`. The platform's allow-list
    (`video/mp4` and `video/quicktime` in development) still refuses others
    with `415`, which is shown with its reason.

## R11. Development object storage and the CSP

- **Decision**:
  - **Allowed origin**: the AppHost sets `RUSTFS_CORS_ALLOWED_ORIGINS` on the
    `rustfs` container to the Web UI endpoint's origin.
  - **CSP origin**: it passes the storage endpoint's origin to the Web UI
    resource as `SOCALYTICS_OBJECT_STORAGE_ORIGIN`. `vite.config.ts` adds that
    origin to `connect-src` in the preview and development policies. Without
    the variable, `connect-src` stays `'self'` and an upload shows a
    storage-unavailable message.
  - **Simulated tests**: the `simulated` project sets the variable to its fake
    storage origin (R14).
- **Rationale**: The AppHost already configures RustFS through its
  environment. A server-wide allow-list is enough for one development bucket
  and needs no platform code. Production storage origins, CORS, and the
  production CSP remain unresolved, as `platform-implementation.md` § Object
  Storage states.
- **Fallback**: if the pinned RustFS `1.0.1` ignores the variable for
  presigned `PUT`, the development bucket initializer applies an S3
  `PutBucketCors` rule with the same origin. Either way the `platform`
  browser tests prove the preflight.
- **Mixed content**: storage is served over `http://localhost`. Chromium treats
  loopback origins as potentially trustworthy, so it is reachable from the Web
  UI origin.

## R12. Trusted Types for the worker

- **Decision**:
  - `src/uploads/workerPolicy.ts` creates the Trusted Types policy
    `upload-worker`. Its `createScriptURL` returns only the bundled worker URL
    and throws for anything else.
  - The CSP directive becomes `trusted-types default react-dom upload-worker`.
  - The foundation's `default` policy stays unchanged.
- **Rationale**: `new Worker(url)` is a Trusted Types sink under
  `require-trusted-types-for 'script'`.

## R13. Analysis run states

- **Decision**: The match view queries `listMatchAnalysisRuns` and groups the
  runs by `recordingSetVersionId`.
  - **Refresh**: TanStack Query refetches every 10 seconds while any run is
    `Requested` or `Running` and the page is visible. It stops once all runs
    are terminal (`PartiallyCompleted`, `Completed`, `Failed`, `Cancelled`).
  - **What is shown**: only state, workflow, timestamps, and the failure
    reason (FR-023).

## R14. Test strategy

- **Unit tests (Vitest)**:
  - Part-size policy, digests against the platform's vectors, and the retry
    and back-off policy.
  - Grant scheduling, the coordinator state machine, and the completion
    refusal mapping.
  - The journal through `fake-indexeddb` (a new exact-pinned dev dependency),
    and sign-out purge.
  - Timeline-mapping conversion (minutes and seconds to seconds) and the
    duplicate-set detection for finalization.
- **`simulated` project**: the API is simulated with `page.route` as in the
  foundation, and its grants point to a fake object store.
  - The fake store is `tests/e2e/support/fakeObjectStorage.mjs`, a Node HTTP
    server that Playwright starts as a second `webServer`. It answers the CORS
    preflight, verifies `x-amz-checksum-sha256` against the body, records
    stored parts, and accepts fault injection through a control endpoint
    (fail part *n* once, slow down, expire grants). A real server is used
    instead of `page.route` because requests from dedicated workers are not
    reliably routed.
  - Test files of 40 MiB (three parts) are generated in the test's temporary
    folder.
  - One test with a 1 GiB file checks SC-006: a `PerformanceObserver`
    reports no main-thread long task over 200 ms while preparing and
    transferring, and navigation stays possible.
  - The API's request log proves that no request to `/api/v1` carries a
    non-JSON body (SC-003). The foundation's storage fixture is extended to
    assert that the journal holds no grant URL or signature (SC-007).
- **`platform` project**: runs against the AppHost `e2e` mode with RustFS. It
  creates a match, uploads a 40 MiB recording, reloads mid-transfer and
  resumes, completes it with a mapping, finalizes the set, and sees the
  analysis run's state.
- **Rationale**: Every acceptance scenario is covered deterministically, and
  the real path through CORS, the presigned grants, and the platform's
  verification is proven once (SC-002).

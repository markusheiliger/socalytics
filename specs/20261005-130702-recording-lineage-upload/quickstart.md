# Quickstart: Recording Lineage and Upload

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Contract**: [contracts/openapi.yaml](contracts/openapi.yaml)

This guide validates the feature end to end once it is implemented. It is a
run guide, not an implementation reference: request and response shapes are
defined in [contracts/openapi.yaml](contracts/openapi.yaml) and
[recordings-finalized.schema.json](contracts/schemas/recordings/recordings-finalized/v1/recordings-finalized.schema.json)
(published at the repository root as
`contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`),
and table rules in [data-model.md](data-model.md). Passing these scenarios is
development evidence only; it is not deployment support or production
readiness (POL-003, THR-003, and the recording owner remain Open / Blocking).

## Prerequisites

- .NET SDK selected by `src/platform/global.json`.
- Docker running locally (Testcontainers starts PostgreSQL and
  `rustfs/rustfs:1.0.1`; the AppHost starts the same images). The opt-in
  above-5-GiB acceptance upload additionally needs about 12 GB of free Docker
  disk (parts plus the assembled object inside the RustFS container).
- The environment feature
  `specs/20261007-115855-environment-verification-coverage` is merged, so
  automated verification runs the platform checks for repository-root
  `contracts/` changes.
- The persistence foundation (`20261005-130700`) and the Club and Identity
  foundation (`20261005-130701`) are implemented, so the Migrator, sessions,
  anti-forgery, `ITeamScopeResolver`, `IAuditTrail`, Seasons, Teams, and
  Matches exist.

## 1. Build and run the automated evidence

From the repository root:

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
```

To run only this feature's tests while iterating:

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~Recordings"
dotnet test src/platform/Tests/SocAlytics.Platform.Architecture.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
```

The first command is the repository contract command (also listed in
`contracts/README.md`); drop `--no-build` when running it on its own.

The above-5-GiB acceptance upload (SC-010) is opt-in because it needs about
12 GB of free Docker disk and several minutes; without the variable it reports
as skipped. Run it explicitly to record the SC-010 evidence:

```powershell
$env:SOCALYTICS_RUN_LARGE_UPLOAD_TEST = 'true'
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build --filter "FullyQualifiedName~LargeRecordingUploadTests"
Remove-Item Env:SOCALYTICS_RUN_LARGE_UPLOAD_TEST
```

**Expected**: all suites pass. The contract tests discover every schema under
`contracts/` through `ContractCatalog` (excluding `**/releases/**`), including
`contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`;
validate it against the JSON Schema 2020-12 meta-schema; confirm that its
`$id` is
`https://socalytics.invalid/contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
that its path follows `contracts/<area>/<name>/v<major>/<name>.schema.json`
(or the shared-definitions form `contracts/common/v<major>/common.schema.json`),
that `x-socalytics-version` is `1.0.0` with major version matching `v1`, that
every `$ref` resolves offline to a catalog schema and pointer, that every
embedded example validates, that a plain `sha-256` recording digest is
rejected, and that the schema has exactly one row in `contracts/README.md`. Breaking the schema or an example makes the contract
command fail and name the file. The integration suite covers the scenarios in
the table below against real PostgreSQL and RustFS containers.

| Scenario | Spec reference | Expected outcome |
| --- | --- | --- |
| Store conformance probe against the RustFS container (composite initiation, signed part checksum and length, `ListParts`, composite completion, `HeadObject`, abort) | research R16 | All probe assertions pass; the same test passes against any store named by `SOCALYTICS_CONFORMANCE_S3_*` before a profile adopts it |
| Coach starts a 3-part upload (5 MiB, 5 MiB, 1 234 567 B), PUTs each part directly to RustFS with its grant, completes with a valid mapping | US1 AS1–AS2, SC-001 | `201`; recording version with `sha-256-parts:5242880:3:<hex>`, `totalSizeBytes = 11720327`, and a `sha-256:` mapping digest; session `completed`; the API received only JSON bodies ≤ 1 MiB |
| Single-part upload (1 KiB) | R3, R4 | `201`; `sha-256-parts:<partSize>:1:<hex>` where hex is SHA-256 of the part digest (not the plain file digest) |
| Upload slightly above 5 GiB with 64 MiB parts (`LargeRecordingUploadTests`, opt-in with `SOCALYTICS_RUN_LARGE_UPLOAD_TEST=true`) | US1 AS11, SC-010 | `201`; recording version records composite digest, part size, part count, and total size; `ListParts` before completion shows exactly the declared sizes |
| Club Admin performs the same workflow on any Team | US1 AS3 | Same as the 3-part case |
| Coach of another Team, Viewer, unauthenticated, archived Season, missing Match | US1 AS4–AS6, FR-005, FR-006, SC-003 | `403`, `403`, `401`, `409 season-archived`, `404`; zero new or changed recording rows and no multipart upload created; denial audit events |
| Invalid declarations: non-final part < 5 MiB, part count inconsistent with total, digest count mismatch, above configured maximum size or part count, empty recording | Edge "Invalid part declaration", FR-007 | `400 upload-declaration-invalid`; no session row and no storage call |
| Part PUT with flipped bytes, wrong or missing checksum header, larger or smaller body, chunked transfer, tampered part number | US1 AS10, FR-008, SC-004 | Storage refuses (`400 BadDigest` or `403`); `ListParts` shows nothing stored for that part; an oversize body is refused before it is read |
| Completion with a missing part, or after a part was stored with another size | US1 AS7, FR-011, SC-004 | `409 upload-parts-incomplete` or `409 upload-part-mismatch`; session stays `pending`; uploading the part with a fresh grant and retrying completion succeeds |
| Storage reports no composite checksum or a different composite, type, or length after assembly (fault-injecting `IObjectStorage` decorator) | FR-011 | `409 integrity-evidence-unavailable` or `409 upload-object-mismatch`; no recording version |
| Completion retried after storage completion succeeded but the transaction was interrupted | R6 recovery | Retry finds `NoSuchUpload`, verifies the assembled object, and returns `201` with one recording version |
| Expired part grants, then fresh grants for some parts | US1 AS8, FR-009 | New grants for the same parts of the same key; a completed, expired, or foreign session yields `409` or `404`; part numbers outside `1…partCount` yield `400 part-numbers-invalid` |
| Grant misuse (GET, DELETE, LIST, complete, other key or part) with a valid part URL | FR-008, edge "Grant misuse" | Storage rejects every request (signature mismatch) |
| Session lifetime passes; worker sweep runs; grant and completion requested | FR-034, edge "Abandoned or partial upload" | Grants and completion return `409 upload-session-expired` immediately after expiry; the sweep marks the session `expired`, aborts the multipart upload (`ListParts` → `NoSuchUpload`), deletes any assembled object, and sets `storage_released_at`; no recording version |
| Completion racing the expiry instant | R7 | Either the recording version commits and the object is kept, or the session expires and no recording version exists; never both |
| Completion retried with the same key; then with a different key | US1 AS9, edge "Duplicate completion" | Original identities returned; second key `409 upload-session-completed` |
| Start replayed with the same key while pending, after completion, and after expiry | FR-009, FR-025, FR-034 | Pending: original session with fresh grants; completed or expired: original session in its current state without grants; no second multipart upload |
| Finalize three recordings in a chosen order; read the set back | US2 AS1, SC-002, SC-005 | `201`; identical members, order, and digests on every read; exactly one row in `recording_finalized_events`, valid against the event schema, with `sha-256-parts` recording digests |
| Empty set, more members than the configured maximum (default 100), cross-Match member, mismatched mapping pair, duplicate recording | US2 AS2–AS4, FR-019, FR-020 | `400` with field violations; no set, member, event, or retry row |
| 10 concurrent finalizations with one key | SC-006, FR-024 | Exactly 1 set version and 1 event record; all callers receive the same set id |
| Injected failure during finalization (test-only database trigger on the event table) | US2 AS6, SC-007 | Request fails; 0 set, member, retry-outcome, or event rows remain |
| `IRecordingSetLookup.GetAsync` for a finalized set | US2 AS8, FR-028, SC-009 | Match, Team, ordered member identities, `sha-256-parts` recording digests, mapping digests, and spans; 0 object-storage calls (storage adapter substitute records none) |
| Revised mapping; identical mapping resubmitted; corrected upload; refinalize | US3 AS1–AS3 | New mapping identity and digest; identical content returns the existing mapping (`200`); new recording version and set version; originals read back unchanged |
| Direct `UPDATE`/`DELETE` on any immutable table as the runtime role and as the migration role | US3 AS4, FR-015, FR-021 | Permission denied (runtime role) and trigger error (migration role); rows unchanged |
| Viewer lists lineage; member of another Team lists lineage | US4 AS1–AS2, FR-027 | `200` with metadata only (no URLs, keys, upload ids, or grants); `403` |
| Key reuse with different content; same key on another Match or operation | FR-025 | `409 idempotency-key-reused`; independent success |
| Revoked Coach replays a previously successful request; Coach revoked before finalization or mapping revision | FR-004, edge "Replay after revocation" | `403`; stored outcome not disclosed; no rows created |
| Lineage reads for a Match in an archived Season | FR-006 | `200` for Club Admin, Coach, and Viewer; mutations still `409 season-archived` |
| Successful start, grant issuance, completion, revision, finalization, and expiry | FR-030 | Exactly one `security_audit_event` row each with its event type, actor, resource, Team, `details.matchId`, outcome, and correlation id; only allow-listed detail keys |
| Object storage unreachable during start, grants, and completion (a test host pointed at an unreachable endpoint over the same database) | Edge "Object storage unavailable" | `503 object-storage-unavailable`; no state change; the same requests then succeed through a host that reaches RustFS |
| Secret scan of database rows, audit events, and captured logs | FR-029, SC-008 | No `X-Amz-Signature`, `X-Amz-Credential`, or storage secret key found |

Structural evidence:

- Architecture tests: Domain and Application reference no `Amazon.*` or
  `Npgsql` types; Recordings Infrastructure implementation types are internal
  (Application handlers are public sealed per the Club convention); only
  `AddApplication()` and `AddInfrastructure()` are public composition
  methods; the AppHost references no S3 SDK.
- Persistence structural test: `recording_upload_sessions` has the version
  trigger and is classified as a versioned root; the immutable Recordings
  tables have no `version` column and carry `socalytics.reject_immutable_change()`;
  no `club_id` column exists.
- Host tests: `/openapi/v1.json` contains every `operationId` of
  [contracts/openapi.yaml](contracts/openapi.yaml) with its documented
  response codes, every unsafe recording operation requires the shared
  `sessionCookie` and `antiforgeryHeader` security schemes, and no recording
  operation accepts a binary or multipart request body.

## 2. Manual end-to-end run through the AppHost

Start the local environment:

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

**Expected**: the Aspire dashboard shows PostgreSQL, the Migrator (completed),
the `rustfs` container (healthy on its `s3` endpoint), and the API (healthy).
The API creates the development bucket at startup because the AppHost sets
`ObjectStorage__EnsureBucketOnStartup=true`. Stop with `Ctrl+C`.

Then, using the API endpoint shown in the dashboard:

1. Sign in as a member who holds Coach on a Team of the active Season, following
   the Club and Identity quickstart, and keep the `__Host-socalytics-session`
   cookie and the `X-CSRF-Token` value for the requests below.
2. Pick or create a Match of that Team and note its id.
3. Split a local test video into parts and compute the declaration (part size
   at least 5 MiB unless the file is a single part):

   ```powershell
   $file = Get-Item .\sample.mp4; $partSize = 8MB
   $stream = $file.OpenRead(); $buffer = New-Object byte[] $partSize; $digests = @(); $n = 0
   while (($read = $stream.Read($buffer, 0, $partSize)) -gt 0) {
       $n++; $chunk = [byte[]]::new($read); [Array]::Copy($buffer, $chunk, $read)
       [IO.File]::WriteAllBytes("$PWD\part$n.bin", $chunk)
       $digests += 'sha-256:' + (Get-FileHash ".\part$n.bin" -Algorithm SHA256).Hash.ToLowerInvariant()
   }
   $stream.Dispose(); "total=$($file.Length) partSize=$partSize parts=$n"; $digests
   ```

4. `POST /api/v1/matches/{matchId}/upload-sessions` with an `Idempotency-Key`,
   the anti-forgery header, and a `StartUploadRequest` body (`totalSizeBytes`,
   `partSizeBytes`, `partDigests`). **Expected**: `201` with
   `session.state = pending`, `session.contentDigest` in the
   `sha-256-parts:<partSize>:<parts>:<hex>` form, `session.expiresAt`, and
   `grants.parts` whose `requiredHeaders` contain `Content-Length` and
   `x-amz-checksum-sha256`.
5. Upload each part directly to storage with its grant:

   ```powershell
   curl.exe -sS -X PUT --data-binary "@part1.bin" -H "Content-Length: <from requiredHeaders>" -H "x-amz-checksum-sha256: <from requiredHeaders>" "<grants.parts[0].url>"
   ```

   **Expected**: `200` from RustFS for every part. The API's request log shows
   no request for these transfers. Sending `part2.bin` to part 1's URL returns
   `403` or `400 BadDigest`.
6. `POST /api/v1/matches/{matchId}/upload-sessions/{sessionId}/completion` with
   a new `Idempotency-Key` and a body such as one span
   `{ "mediaStartSeconds": 0, "mediaEndSeconds": 2700, "matchStartSeconds": 0 }`.
   **Expected**: `201` with the recording version (its `contentDigest` equals
   `session.contentDigest`, its `totalSizeBytes` equals the file size) and the
   timeline mapping with a `sha-256:` digest.
7. Repeat steps 3–6 for two more recordings, then
   `POST /api/v1/matches/{matchId}/recording-set-versions` with the three
   pairs in the desired order. **Expected**: `201` with members in that order.
8. `GET /api/v1/matches/{matchId}/recording-lineage`. **Expected**: all three
   recording versions, their mappings, and the set version with ordered
   members and digests; no URLs, keys, upload ids, or grants.
9. Start one more upload and leave it incomplete. After the configured
   development session lifetime and one sweep interval, `GET` the session.
   **Expected**: `state = expired`; the dashboard's RustFS bucket holds no
   object or parts for it.
10. Inspect the database through the dashboard's PostgreSQL resource:
    `recording_finalized_events` holds exactly one row for the set version, and
    no table contains a grant URL.

## 3. Cleanup

Stopping the AppHost leaves its containers' data in Aspire-managed state.
Abandoned sessions release their stored parts on expiry; retention of accepted
recordings remains governed by POL-003. Delete the local `part*.bin` files.
Testcontainers removes its containers after each test run.

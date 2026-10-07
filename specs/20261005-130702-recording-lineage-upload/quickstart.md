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
  `rustfs/rustfs:1.0.1`; the AppHost starts the same images).
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

**Expected**: all suites pass. The contract tests find
`contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
validate it against the JSON Schema 2020-12 meta-schema, confirm that its
`$id` is
`https://socalytics.invalid/contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json`,
that `x-socalytics-version` is `1.0.0` with major version matching `v1`, that
every embedded example validates, and that the schema is listed in
`contracts/README.md`. Breaking the schema or an example makes the contract
command fail and name the file. The integration suite covers the scenarios in
the table below against real PostgreSQL and RustFS containers.

| Scenario | Spec reference | Expected outcome |
| --- | --- | --- |
| Coach starts upload, PUTs bytes with the grant directly to RustFS, completes with a valid mapping | US1 AS1–AS2, SC-001 | `201`; recording version and mapping with `sha-256:` digests; session `completed`; the API received only JSON bodies ≤ 1 MiB |
| Club Admin performs the same workflow on any Team | US1 AS3 | Same as above |
| Coach of another Team, Viewer, unauthenticated, archived Season, missing Match | US1 AS4–AS6, FR-005, FR-006, SC-003 | `403`, `403`, `401`, `409 season-archived`, `404`; zero new or changed recording rows; denial audit events |
| Completion with no object, wrong size, or storage reporting no SHA-256 | US1 AS7, FR-011, SC-004 | `409` (`upload-object-missing`, `upload-object-mismatch`, `integrity-evidence-unavailable`); session stays `pending`; retry after correction succeeds |
| PUT body that does not match the signed checksum | R3 | RustFS rejects the PUT (`400 BadDigest`); later completion reports `upload-object-missing` |
| Expired grant, then fresh grant for the same session | US1 AS8, FR-009 | New grant for the same key; a completed or foreign session yields `409` or `404` |
| Grant misuse (GET, DELETE, LIST, other key) with a valid grant URL | FR-008 | Storage rejects every request (signature mismatch) |
| Completion retried with the same key; then with a different key | US1 AS9, edge "Duplicate completion" | Original identities returned; second key `409 upload-session-completed` |
| Finalize three recordings in a chosen order; read the set back | US2 AS1, SC-002, SC-005 | `201`; identical members, order, and digests on every read; exactly one row in `recording_finalized_events`, valid against the event schema |
| Empty set, cross-Match member, mismatched mapping pair, duplicate recording | US2 AS2–AS4, FR-020 | `400` with field violations; no set, member, event, or retry row |
| 10 concurrent finalizations with one key | SC-006, FR-024 | Exactly 1 set version and 1 event record; all callers receive the same set id |
| Injected failure during finalization (test-only database trigger on the event table) | US2 AS6, SC-007 | Request fails; 0 set, member, retry-outcome, or event rows remain |
| `IRecordingSetLookup.GetAsync` for a finalized set | US2 AS8, FR-028, SC-009 | Match, Team, ordered member identities and digests; 0 object-storage calls (storage adapter substitute records none) |
| Revised mapping; identical mapping resubmitted; corrected upload; refinalize | US3 AS1–AS3 | New mapping identity and digest; identical content returns the existing mapping (`200`); new recording version and set version; originals read back unchanged |
| Direct `UPDATE`/`DELETE` on any immutable table as the runtime role and as the migration role | US3 AS4, FR-015, FR-021 | Permission denied (runtime role) and trigger error (migration role); rows unchanged |
| Viewer lists lineage; member of another Team lists lineage | US4 AS1–AS2, FR-027 | `200` with metadata only (no URLs, keys, or grants); `403` |
| Key reuse with different content; same key on another Match or operation | FR-025 | `409 idempotency-key-reused`; independent success |
| Revoked Coach replays a previously successful request | FR-004, edge "Replay after revocation" | `403`; stored outcome not disclosed |
| Object storage stopped during start, grant, and completion | Edge "Object storage unavailable" | `503 object-storage-unavailable`; no state change |
| Secret scan of database rows, audit events, and captured logs | FR-029, SC-008 | No `X-Amz-Signature`, `X-Amz-Credential`, or storage secret key found |

Structural evidence:

- Architecture tests: Domain and Application reference no `Amazon.*` or
  `Npgsql` types; Recordings implementation types are internal; only
  `AddApplication()` and `AddInfrastructure(...)` are public composition
  methods.
- Persistence structural test: `recording_upload_sessions` has the version
  trigger; no immutable Recordings table has a `version` column; no
  `club_id` column exists.
- Host tests: `/openapi/v1.json` contains every `operationId` of
  [contracts/openapi.yaml](contracts/openapi.yaml) with its documented
  response codes, and no recording operation accepts a binary or multipart
  request body.

## 2. Manual end-to-end run through the AppHost

Start the local environment:

```powershell
dotnet run --project src/platform/SocAlytics.Platform.AppHost
```

**Expected**: the Aspire dashboard shows PostgreSQL, the Migrator (completed),
the `objectstorage` RustFS container (healthy), and the API (healthy). The
AppHost creates the development bucket after RustFS reports healthy. Stop with
`Ctrl+C`.

Then, using the API endpoint shown in the dashboard:

1. Sign in as a member who holds Coach on a Team of the active Season, following
   the Club and Identity quickstart, and keep the session cookie and
   anti-forgery token for the requests below.
2. Pick or create a Match of that Team and note its id.
3. Compute the size and digest of a small local test video:

   ```powershell
   $file = Get-Item .\sample.mp4
   $hex = (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant()
   "size=$($file.Length) digest=sha-256:$hex"
   ```

4. `POST /api/v1/matches/{matchId}/upload-sessions` with an `Idempotency-Key`,
   the anti-forgery header, and a `StartUploadRequest` body. **Expected**:
   `201` with `session.state = pending` and a `grant` whose
   `requiredHeaders` contain `Content-Type` and `x-amz-checksum-sha256`.
5. Upload the file directly to storage with the grant:

   ```powershell
   curl.exe -sS -X PUT -T .\sample.mp4 -H "Content-Type: <from requiredHeaders>" -H "x-amz-checksum-sha256: <from requiredHeaders>" "<grant.url>"
   ```

   **Expected**: `200` from RustFS. The API's request log shows no request for
   this transfer.
6. `POST /api/v1/matches/{matchId}/upload-sessions/{sessionId}/completion` with
   a new `Idempotency-Key` and a body such as one span
   `{ "mediaStartSeconds": 0, "mediaEndSeconds": 2700, "matchStartSeconds": 0 }`.
   **Expected**: `201` with the recording version and timeline mapping, both
   with `sha-256:` digests; the recording's `contentDigest` equals the value
   from step 3.
7. Repeat steps 4–6 for two more recordings, then
   `POST /api/v1/matches/{matchId}/recording-set-versions` with the three
   pairs in the desired order. **Expected**: `201` with members in that order.
8. `GET /api/v1/matches/{matchId}/recording-lineage`. **Expected**: all three
   recording versions, their mappings, and the set version with ordered
   members and digests; no URLs or grants.
9. Inspect the database through the dashboard's PostgreSQL resource:
   `recording_finalized_events` holds exactly one row for the set version, and
   no table contains the grant URL.

## 3. Cleanup

Stopping the AppHost leaves its containers' data in Aspire-managed state;
abandoned sessions and objects are not cleaned up automatically (deferred,
POL-003). Testcontainers removes its containers after each test run.

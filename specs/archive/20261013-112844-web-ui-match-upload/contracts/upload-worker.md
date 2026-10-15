# Contract: Upload Worker Protocol

The upload worker (`src/uploads/worker/uploadWorker.ts`) and the main-thread
`UploadCoordinator` exchange typed messages through `postMessage`. Screens use
only the coordinator and `UploadRepository`; nothing else talks to the worker
(research.md § R1).

## Main thread → worker

| Message | Payload | Effect |
| --- | --- | --- |
| `prepare` | `jobId`, `file`, `partSizeBytes` (optional; computed when absent) | Hash parts, reply `prepared` or `prepareFailed`; cancellable |
| `verify` | `jobId`, `file`, `partSizeBytes`, `expectedContentDigest` | Rehash and compare, reply `verified` or `verifyFailed(fileMismatch)` |
| `journalPut` | `record` (data-model.md § Journal record, without grants) | Create or update the record |
| `journalList` | `memberId`, optional `matchId` | Reply `journalRecords` |
| `journalDelete` | `memberId`, `uploadSessionId` | Delete one record |
| `purgeMember` | `memberId` | Abort the member's transfers, delete all their records, reply `purged` |
| `transfer` | `jobId`, `uploadSessionId`, `file`, `declaration`, `confirmedParts` | Acquire `socalytics-upload-slot` and `socalytics-upload:<id>`; reply `needGrants` as needed |
| `grants` | `jobId`, `grants[]` (`partNumber`, `url`, `requiredHeaders`, `expiresAt`) | Kept in memory only; used for the next parts |
| `pause` / `resume` / `abort` | `jobId` | Control transfers; `abort` releases locks |

## Worker → main thread

| Message | Payload |
| --- | --- |
| `prepared` | `jobId`, `partDigests[]`, `contentDigest`, `partSizeBytes`, `partCount` |
| `prepareProgress` | `jobId`, `bytesHashed`, `totalBytes` |
| `prepareFailed` | `jobId`, `reason` (`readError`, `cancelled`) |
| `verified` / `verifyFailed` | `jobId`, `reason` |
| `needGrants` | `jobId`, `partNumbers[]` (at most 32, R4) |
| `transferProgress` | `jobId`, `bytesConfirmed`, `bytesInFlight`, `confirmedParts[]` |
| `partFailed` | `jobId`, `partNumber`, `status`, `retryable`, `attempt` |
| `paused` | `jobId`, `reason` (`retriesExhausted`, `storageUnavailable`, `runningElsewhere`) |
| `transferred` | `jobId` (every part confirmed) |
| `failed` | `jobId`, `reason` (`fileChanged`) |
| `journalRecords` / `purged` | records / `memberId` |

## Rules

- The worker never calls `/api/v1/**`; a unit test asserts it imports nothing
  from `src/platform/`.
- Grants, URLs, and required headers are never written to IndexedDB, never
  logged, and dropped when a job ends.
- The worker sets exactly the grant's `x-amz-checksum-sha256` header and lets
  the browser derive `Content-Length` (research.md § R3).
- Journal writes happen after each part confirmation and before every state
  change that a reload must survive (start key, session identity, completion
  key).

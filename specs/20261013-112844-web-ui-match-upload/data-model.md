# Data Model: Web UI Match Workflow and Recording Upload

The Web UI holds no authoritative data. The platform's matches, upload
sessions, recording versions, timeline mappings, recording-set versions, and
analysis runs are read through the adapters
([contracts/platform-adapter.md](contracts/platform-adapter.md)) and cached by
TanStack Query with their versions. The only data persisted in the browser is
the [journal record](#journal-record).

## Match (client view)

Source: `MatchDto` (`listTeamMatches`, `getMatch`, `createMatch`,
`updateMatch`).

| Field | Use |
| --- | --- |
| `id`, `teamId` | Routes, authorization checks through the session's team roles |
| `opponent.name` | Shown; editable only at creation (FR-002) |
| `kickoffAt`, `homeAway` (`home`, `away`, or `neutral`), `competition` | Shown and editable |
| `version` | `If-Match` on update (FR-003) |

Editable only when the member is Club Admin or Coach of `teamId` and the team's
`seasonState` is not `archived` (the platform's `season-archived` refusal stays
authoritative).

## Upload job (in memory, main thread)

One per file the member added in this browser session or resumed from the
journal.

| Field | Notes |
| --- | --- |
| `localId` | Client identity until the platform session exists |
| `matchId`, `teamId`, `memberId` | Scope |
| `file` | The `File` handle; absent after a reload until reselected |
| `displayName`, `description`, `contentType` | From the member and `file.type` |
| `declaration` | `totalSizeBytes`, `partSizeBytes`, `partCount`, `partDigests[]`, `contentDigest` (R2) |
| `uploadSessionId`, `sessionExpiresAt` | After start |
| `parts[]` | Per part: `state` (`pending`, `transferring`, `confirmed`, `failed`), `attempts`, `bytesSent` |
| `durationSeconds` | From the local file (R9), or absent |
| `mappingDraft` | See [timeline mapping draft](#timeline-mapping-draft) |
| `startKey`, `completionKey` | Idempotency keys (R8) |
| `state`, `reason` | See below |

### States

```text
selected ─► preparing ─► ready ─► starting ─► queued ─► transferring ─► awaitingMapping ─► completing ─► completed
                │                                  ▲        │   ▲                              │
                ▼                                  │        ▼   │                              ▼
            cancelled                              └──── paused(reason)                    refused(reason) ─► queued
any non-final state ─► abandoned | expired | notPermitted | failed(reason)
needsFile (after reload, before reselection) ─► preparing (verification) ─► queued | refused(fileMismatch)
```

- `paused` reasons: `retriesExhausted`, `unreachable`, `storageUnavailable`,
  `sessionEnded`, `runningElsewhere`.
- `failed` reasons: `fileChanged`, `objectMismatch`, `integrityUnavailable`.
- `refused` reasons: `partsIncomplete`, `partMismatch` (R7).
- `awaitingMapping` is skipped when the member entered the mapping before the
  transfer finished; completion starts as soon as every part is confirmed.
- Final states: `completed`, `abandoned`, `expired` (after acknowledgement),
  `cancelled`, `failed`. Their journal records are deleted.
- `notPermitted` (membership or role revoked) stops all transfers and offers
  only "abandon"; it becomes `abandoned` when the member abandons it.

## Journal record

The spec's *local upload record*. IndexedDB `socalytics-upload-journal`, store
`uploads`, key `[memberId, uploadSessionId]`, index `[memberId, matchId]`;
owned by the upload worker (research.md § R6).

| Field | Notes |
| --- | --- |
| `memberId`, `matchId`, `teamId` | Identifiers (scope) |
| `uploadSessionId` | Identifier; absent while `starting`, then set |
| `startKey`, `completionKey` | Identifiers (idempotency keys) |
| `fileName`, `fileSizeBytes`, `fileLastModified` | File name and size; the timestamp only detects an obviously different file before rehashing |
| `partSizeBytes`, `partCount`, `contentDigest` | Declaration summary for verification |
| `confirmedParts` | Sorted part numbers confirmed as stored |
| `sessionExpiresAt`, `updatedAt` | Display and housekeeping |

Exactly these fields, as FR-014 allows. The display name, description, and
content type are read again from the upload session
(`getRecordingUploadSession`); a timeline mapping draft is not remembered and
is entered again after a reload. Never contains grant URLs, signatures,
required headers, session secrets, or file contents (FR-014, SC-007). All
records of a member are deleted on explicit sign-out; a record is deleted when
its job reaches a final state.

## Timeline mapping draft

| Field | Rule |
| --- | --- |
| `spans[]` | One or more `{ mediaStartSeconds, mediaEndSeconds, matchStartSeconds }` |
| Default | One span `0 → durationSeconds` starting at the match time the member enters (clarification); without a duration the member enters `mediaEndSeconds` |
| Input | `mm:ss` or `hh:mm:ss`, converted to seconds with millisecond precision |
| Client checks | Required fields present and numeric; the platform's validation (overlaps, ordering, bounds) is authoritative and shown per span |

Sent as `TimelineMappingContentDto` with completion (`completeRecordingUpload`)
or a revision (`reviseRecordingTimelineMapping`).

## Finalize selection

| Field | Rule |
| --- | --- |
| `members[]` | Ordered `{ recordingVersionId, timelineMappingId }`; default mapping is the newest mapping of the version |
| Limits | Not empty; at most the platform's limit (100 by default, refusal authoritative) |
| `isIdenticalToNewest` | True when the order, versions, and mappings equal the newest finalized set; requires explicit confirmation (clarification) |
| `idempotencyKey` | One per open finalize dialog (R8) |

## Lineage (client view)

Source: `RecordingLineageDto` (`getMatchRecordingLineage`): recording versions,
timeline mappings, and recording-set versions with ordered members and digests;
read-only (FR-021, FR-022). Recording versions' `contentDigest` values are also
used to warn about a file that was already uploaded (edge case).

## Analysis run (state only)

Source: `AnalysisRunSummaryDto` (`listMatchAnalysisRuns`): `runId`,
`recordingSetVersionId`, `workflowId`, `workflowVersion`, `state`,
`failureReason`, `createdAt`, `runningAt`, `terminatedAt`. Grouped by
recording-set version; refreshed while `Requested` or `Running` (R13).

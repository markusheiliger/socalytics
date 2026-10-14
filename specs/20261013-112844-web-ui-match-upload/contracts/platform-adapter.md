# Contract: Platform Adapters (additions)

Extends the foundation's
[platform-adapter.md](../../archive/20261012-150657-web-ui-foundation/contracts/platform-adapter.md).
Only `src/platform/` imports the generated client; features receive the Web
UI's own models ([data-model.md](../data-model.md)).

## Adapters

| Adapter | Operations (OpenAPI operation id) | Returns |
| --- | --- | --- |
| `MatchAdapter` | `listByTeam(teamId, page)` (`listTeamMatches`), `get(id)` (`getMatch`), `create(teamId, input)` (`createMatch`), `update(id, input, version)` (`updateMatch`, `If-Match`) | `Match`, `Page<Match>` |
| `RecordingUploadAdapter` | `start(matchId, descriptor, declaration, key)` (`startRecordingUpload`), `getSession(matchId, id)` (`getRecordingUploadSession`), `issueGrants(matchId, id, partNumbers)` (`issueRecordingUploadGrants`), `complete(matchId, id, mapping, key)` (`completeRecordingUpload`) | `UploadSession`, `PartGrant[]` (in memory only), `CompletedUpload` |
| `RecordingLineageAdapter` | `getLineage(matchId)` (`getMatchRecordingLineage`), `getVersion(matchId, id)` (`getRecordingVersion`), `reviseMapping(matchId, versionId, mapping, key)` (`reviseRecordingTimelineMapping`), `finalize(matchId, members, key)` (`finalizeRecordingSet`), `getSet(matchId, id)` (`getRecordingSetVersion`) | `Lineage`, `RecordingVersion`, `TimelineMapping`, `RecordingSetVersion` |
| `AnalysisRunAdapter` | `listByMatch(matchId, page)` (`listMatchAnalysisRuns`) | `AnalysisRunSummary[]` |

Every operation that the API marks with `Idempotency-Key` receives the key from
the caller (research.md § R8); adapters never generate keys themselves, so a
retry of the same intent always reuses its key.

## Error mapping additions (`PlatformFailure`)

| Platform response | `PlatformFailure.kind` | Web UI behavior |
| --- | --- | --- |
| `409 season-archived` | `seasonArchived` | Read-only notice on the team or match |
| `400 upload-declaration-invalid` (field errors) | `validation` | Reason shown on the file; no upload created |
| `413` | `tooLarge` | "The recording is larger than the platform allows" |
| `415` | `unsupportedType` | "This video format is not accepted" |
| `409 upload-session-expired` | `uploadExpired` | Job → `expired` |
| `409 upload-session-completed` | `uploadCompleted` | Replay: read the lineage and treat as completed |
| `409 upload-parts-incomplete`, `409 upload-part-mismatch` | `uploadPartsRefused` | Job → `refused`, parts re-marked (R7) |
| `409 upload-object-mismatch`, `409 integrity-evidence-unavailable` | `uploadIntegrityFailed` | Job → `failed` with the reason |
| `503` from upload operations | `storageUnavailable` | Job → `paused(storageUnavailable)`, retried on recovery |
| `400 idempotency-key-missing`, `409 idempotency-key-reused` | `programmingError` | Generic error; covered by unit tests |
| `400` timeline mapping field errors | `validation` | Reason shown next to the affected span |

The foundation's mappings (`sessionEnded`, `notPermitted`, `conflict`,
`unreachable`, `unexpected`, `incompatible`) apply unchanged.

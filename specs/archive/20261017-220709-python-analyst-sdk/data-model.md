# Data Model: Python Analyst SDK

No database tables or migrations. The entities are contract documents and SDK objects.

## Contract documents

| Document | Location in the container | Contract | Writer | Reader |
| --- | --- | --- | --- | --- |
| Logical Job | `/socalytics/job.json` (read-only) | `analysis/analyst-job` 1.0.0, 1.1.0 | platform (published), Manager (copied verbatim) | SDK |
| Attempt Context | `/socalytics/attempt.json` (read-only) | `analysis/analyst-attempt` 1.0.0 (new) | Manager | SDK |
| Staged coverage | `/socalytics/inputs/{capabilityId}/coverage.json` | `{ upstream, coverage }` parts of `analysis/upstream-results` 1.0.0, 1.1.0; states no version and is validated against the newest supported definitions | Manager | SDK |
| Staged manifest | `/socalytics/inputs/{capabilityId}/{acceptedResultId}/manifest.json` | `analysis/result-manifest` 1.0.0, 1.1.0 | upstream Analyst (via Manager) | SDK |
| Staged artifact | `/socalytics/inputs/{capabilityId}/{acceptedResultId}/{name}` | bytes; fact artifacts per `analysis/fact-artifact` 1.0.0 | upstream Analyst (via Manager) | SDK |
| Fact artifact | `/socalytics/result/facts.json` | `analysis/fact-artifact` 1.0.0, media type `application/vnd.socalytics.facts+json` | SDK | Manager, platform |
| Supporting artifact | `/socalytics/result/{name}` | bytes; `name` per the result-manifest `objectName` | SDK (author's bytes) | Manager, platform |
| Result manifest | `/socalytics/result/manifest.json` | `analysis/result-manifest` 1.1.0 | SDK | Manager, platform |

### Logical Job additions (analyst-job 1.1.0)

| Field | Type | Presence | Value |
| --- | --- | --- | --- |
| `workflow.id` | common `key` | every 1.1.0 message from the platform | the run's workflow id |
| `workflow.version` | common `semanticVersion` | with `workflow` | the run's workflow version, as in run-state-changed |
| `segmentWindow.segmentId` | common `identifier` | segment-scoped jobs only | `{recordingVersionId}-{segmentNumber}` |
| `segmentWindow.startMs` | integer ≥ 0 | with `segmentWindow` | `(segmentNumber − 1) · D` |
| `segmentWindow.endMs` | integer ≥ 1 | with `segmentWindow` | `segmentNumber · D`, `D` = segmentation policy duration in ms |

Both fields are optional in the schema so 1.0.0 messages stay valid; `segmentWindow` on a match-scoped job is invalid.

### Attempt Context (analyst-attempt 1.0.0)

| Field | Type | Rule |
| --- | --- | --- |
| `contractVersion` | common `contractVersionV1` | `1.0.0` |
| `attemptId` | common `identifier` | the claimed Execution Attempt |
| `attemptNumber` | common `attemptNumber` | the claimed attempt number |

Closed object; no lease, fencing token, grant, credential, or address.

## SDK objects

| Object | Holds | Rules |
| --- | --- | --- |
| `AnalystContext` | `job`, `attempt`, `inputs`, `result`, `scratch` path | built by the entry point; read-only except `result` |
| `LogicalJob` | typed analyst-job | 1.1.0 fields required to finalize (`job-context-incomplete` otherwise) |
| `AttemptContext` | typed analyst-attempt | — |
| `StagedInputs` | one `StagedDependency` per `inputs[]` entry | verified on load (research R8) |
| `StagedDependency` | upstream node key, capability, edge kind, input-snapshot status, `coverage` (or none), `results` | `results` empty unless status `accepted` |
| `StagedResult` | accepted result id, typed manifest, `facts` (typed, from fact artifacts), `artifacts` (verified read-only paths) | digest and size verified |
| `Fact` | `kind` (key), `startMs`, `endMs`, `confidence` (0..1), `payload` (JSON object) | `startMs < endMs`; inside the validity window |
| `ResultWriter` | output type, confidence, evidence references, validity window, facts, supporting artifacts | states below |

### ResultWriter states

```text
Open --add_fact / add_artifact / set_*--> Open
Open --finalize (all checks pass)--> Finalized   (manifest exists)
Open --finalize (a check fails)--> Failed       (no manifest; staging removed)
Finalized --any mutation or finalize--> error `result-already-finalized`
```

Finalization checks, in order: validity window present and within bounds; every fact inside it; no top-level file in
the result mount that the writer did not write; documents valid against the canonical schemas; then artifacts are
moved, and the manifest is moved last.

## Error categories

| Category | Exit | Raised when |
| --- | --- | --- |
| `input-missing` | 2 | `job.json` or `attempt.json` is absent or unreadable |
| `input-invalid` | 2 | a read document fails its canonical schema or model |
| `unsupported-contract-version` | 2 | a document states another major or a newer minor version |
| `job-context-incomplete` | 2 | the job lacks `workflow`, or a segment job lacks `segmentWindow` |
| `input-staging-mismatch` | 2 | staged results differ from the input snapshot, or a manifest checksum differs |
| `input-artifact-mismatch` | 2 | a staged artifact is missing or its size or digest differs |
| `fact-out-of-window` | 1 | a fact range is empty or outside the validity window |
| `fact-invalid` | 1 | a fact or payload violates the fact-artifact contract |
| `artifact-invalid` | 1 | a supporting artifact name is invalid, reserved, duplicate, or its content is empty |
| `validity-window-missing` | 1 | a match job has no stated or derivable window |
| `validity-window-invalid` | 1 | a stated window is empty or outside the segment window |
| `confidence-missing` | 1 | facts were added but no overall confidence was set |
| `result-value-invalid` | 1 | an overall confidence outside 0..1 or an evidence reference that is not an identifier |
| `result-unexpected-file` | 1 | the result mount holds a top-level file the writer did not write |
| `result-already-finalized` | 1 | mutation or finalization after finalization |
| `output-invalid` | 1 | a written document fails its canonical schema (an SDK defect) |
| `analyst-failed` | 1 | the author's function raised any other exception |

# Contract: Agent Tools API (`/api/v1/agent-tools`)

Every operation is in the OpenAPI document with the tag `Agent tools`, its stable operation identifier, and typed
request and result schemas, and uses the existing problem details (`urn:socalytics:problem:<code>`). Field names are
camelCase JSON. The route group contains exactly these eleven operations (`MapAgentToolApi()`, policy `AgentWorkload`,
HTTPS required); member sessions, Analyst Manager tokens, and anonymous callers get `401` (audited as
`agent.tool.call.refused` with reason `caller-not-workload`).

## Common request and response

`POST /api/v1/agent-tools/attempts/{attemptId}/{tool-path}` with
`Authorization: DPoP <token>`, a `DPoP` proof, and

```json
{ "callId": "call-0001", "fencingToken": "…", "arguments": { } }
```

Success `200`:

```json
{ "callId": "call-0001", "toolId": "get_match", "outcome": "answered", "result": { } }
```

`outcome` is `answered` or `unavailable`; `unavailable` results are
`{ "reason": "capability-not-in-run" | "no-accepted-producer", "producingCapability": "formation-analysis" | null, "detail": "…" }`.

## Operations

| Tool | Operation id | Path segment | Arguments | Result |
| --- | --- | --- | --- | --- |
| `get_match` | `agentToolGetMatch` | `get-match` | `{ facts?: { capabilityId, capabilityVersion, schemaVersion, nodeKey?, fromMs?, toMs?, pageSize?, continuationToken? } }` | `{ match: { matchId, teamId, opponent, kickoffAt, homeAway, competition }, run: { runId, state, isCurrent, supersededBy }, streams: [fact-stream entries of listAnalysisRunFactStreams], facts?: { stream, range, items, coverage, continuationToken } }` |
| `get_video_clip` | `agentToolGetVideoClip` | `get-video-clip` | exactly one of `fact { acceptedResultId, ordinal, contextBeforeMs?, contextAfterMs? }` and `range { fromMs, toMs }`; `retryFailed?` | the `resolveAnalysisEvidenceMedia` response (`run`, `requested`, `fact`, `shortened`, `parts`) |
| `get_player_stats` | `agentToolGetPlayerStats` | `get-player-stats` | `{ fromMs?, toMs? }` | always `unavailable`, `producingCapability: null` |
| `find_counter_attacks` | `agentToolFindCounterAttacks` | `find-counter-attacks` | `{ fromMs?, toMs? }` | always `unavailable`, `counter-attack-analysis` |
| `find_turnovers` | `agentToolFindTurnovers` | `find-turnovers` | `{ fromMs?, toMs? }` | always `unavailable`, `possession-and-transitions` |
| `get_heatmap` | `agentToolGetHeatmap` | `get-heatmap` | `{ fromMs?, toMs? }` | always `unavailable`, `spatial-aggregation` |
| `find_ball_actions` | `agentToolFindBallActions` | `find-ball-actions` | `{ fromMs?, toMs? }` | always `unavailable`, `ball-action-analysis` |
| `find_set_plays` | `agentToolFindSetPlays` | `find-set-plays` | `{ fromMs?, toMs? }` | always `unavailable`, `set-play-analysis` |
| `get_formations` | `agentToolGetFormations` | `get-formations` | `{ fromMs?, toMs? }` | always `unavailable`, `formation-analysis` |
| `find_pressing_phases` | `agentToolFindPressingPhases` | `find-pressing-phases` | `{ fromMs?, toMs? }` | always `unavailable`, `pressing-analysis` |
| `get_tactical_phases` | `agentToolGetTacticalPhases` | `get-tactical-phases` | `{ fromMs?, toMs? }` | always `unavailable`, `tactical-phase-aggregation` |

Arguments never contain a team, match, conversation, or run identifier; the scope is the invocation's team, match,
and the conversation's bound run. Responses of `get_video_clip` carry links only in `parts[].media.url`; the platform
never stores or logs them.

## Problems

| Status | Code | When | Lineage entry |
| --- | --- | --- | --- |
| `400` | validation (field violations) | body without `callId` or `fencingToken`, or otherwise malformed so the call cannot be bound | none |
| `400` | validation (field violations), `analysis-capability-ambiguous` | arguments outside the bounds of the reused reads | `invalid` |
| `401` | — | no workload token | none (audited) |
| `403` | `agent-tool-not-permitted` | tool not in the invocation's pinned tool policy | `not-permitted` |
| `404` | not found | unknown attempt; fact not part of the bound run | none for an unknown attempt; `not-found` otherwise |
| `409` | `agent-attempt-stale` (`disposition`: `attempt-not-current`, `lease-expired`, `attempt-fenced`, `invocation-ended`, `scope-lost`) | binding fails, or scope loss detected by this call (attempt fenced) | none, except `scope-lost` detected by this call |
| `409` | `agent-tool-call-id-reused` | call identifier already used in the attempt | none |
| `429` | `agent-tool-call-limit-reached` (`limit`: `calls` or `clip-resolutions`) | per-attempt limit exhausted | `limit-reached` |
| `503` | `evidence-storage-unavailable` | storage could not confirm a media checksum | `failed` |

## Machine-readable contract (`contracts/agent-tools/tool-allowlist/v1`)

`{ schemaVersion: "1.0.0", allowlistVersion: "1.0.0", tools: [{ toolId, operationId, method: "POST", pathTemplate,
answers: "accepted-evidence" | "analysis-unavailable", producingCapability, argumentsSchema, resultSchema }] }`;
additional properties are refused; `toolId` matches `^[a-z][a-z0-9_]{0,63}$` (no `()`). Fixtures: valid
`allowlist-1.0.0.json` (the eleven tools above); invalid fixtures with a tool id containing `()`, an entry with a
`DELETE` method, an unknown `answers` value, and an extra property.

## Changed orchestration read

`getAgentInvocationLineage` (owner only, unchanged authorization) returns `tool-call` entries with the payload of
[data-model.md](../data-model.md#agent_lineage_entries-existing-changed); the lineage response schema gains the
`tool-call` kind.

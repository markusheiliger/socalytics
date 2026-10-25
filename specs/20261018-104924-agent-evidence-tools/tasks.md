---

description: "Task list for Agent Evidence Tools API"
---

# Tasks: Agent Evidence Tools API

**Input**: Design documents from `specs/20261018-104924-agent-evidence-tools/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: The spec requires automated evidence (FR-021, SC-001 to SC-005), so every story has test tasks. Test tasks follow the implementation tasks of their story so that each landed task keeps the build and test suites green.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story. This feature changes the code of `specs/archive/20261017-204332-agent-orchestration-invocations`, `specs/archive/20261018-101517-agent-catalog-registry`, `specs/archive/20261017-175648-fact-indexing-queries`, and `specs/archive/20261017-224326-evidence-clip-access` in place and is implemented after they and `specs/archive/20261017-183953-analysis-run-supersession` have merged.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Consecutive `[P]` tasks under one heading run in parallel, each from the same commit; they change disjoint files and do not depend on each other
- **[Story]**: Which user story this task belongs to (US1, US2, US3)
- Every task names its exact file paths

---

## Phase 1: Foundational (Blocking Prerequisites)

**Purpose**: Contract, schema, domain, shared read cores, tool-call binding, stores, audit, and metrics that every story needs

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

### Contract, schema, domain, and read cores

- [x] T001 [P] Add the JSON Schema 2020-12 contract `contracts/agent-tools/tool-allowlist/v1/tool-allowlist.schema.json` with `releases/1.0.0.schema.json`, the valid fixture `examples/valid/allowlist-1.0.0.json` (the eleven tools, operation identifiers, path segments, answer kinds, and producing capabilities of `specs/20261018-104924-agent-evidence-tools/contracts/agent-tools-api.md`), invalid fixtures ("a tool id containing `()`, an entry with a `DELETE` method, an unknown `answers` value, and an extra property") with `expectations.json`, following `contracts/agent-catalog/catalog-baseline/v1`, and add its index row to `contracts/README.md` following the existing `$id` convention (FR-001; research R3)
- [x] T002 [P] Add the forward-only migration `src/platform/SocAlytics.Platform.Infrastructure/Persistence/Migrations/NNNN_agentorchestration_tool_calls.sql`, numbered as the next free number on an up-to-date default branch, with immutable `socalytics.agent_tool_calls` (primary key (`attempt_id`, `call_id`), `call_id` check "`^[A-Za-z0-9._\-]{1,128}$`", `invocation_id` foreign key to `agent_invocations`, `tool_id` check over the eleven tool identifiers, `arguments_digest`, `is_clip_resolution`, `reserved_at`) protected by `socalytics.reject_immutable_change()`, replace the `kind` check of `socalytics.agent_lineage_entries` so it also allows `tool-call`, and add a partial unique index on `agent_lineage_entries` (`attempt_id`, `(payload->>'callId')`) where `kind = 'tool-call'`; never edit an applied migration; classify `agent_tool_calls` as `Immutable` in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/Structure/PersistedTableClassifications.cs` (FR-007a, FR-007b, FR-016; data-model.md)
- [x] T003 [P] Add `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Tools/AgentToolAllowlist.cs` (allowlist version `1.0.0` and the eleven entries: tool identifier, operation identifier, path segment, answer kind `accepted-evidence` or `analysis-unavailable`, producing capability `counter-attack-analysis`, `possession-and-transitions`, `spatial-aggregation`, `ball-action-analysis`, `set-play-analysis`, `formation-analysis`, `pressing-analysis`, `tactical-phase-aggregation`, or `null` for `get_player_stats`) and `src/platform/SocAlytics.Platform.Domain/AgentOrchestration/Tools/ToolCallOutcome.cs` (outcomes "`answered`, `unavailable`, `not-found`, `not-permitted`, `invalid`, `limit-reached`, `scope-lost`, `failed`", reasons `capability-not-in-run`, `no-accepted-producer`, `evidence-storage-unavailable`, `interrupted`, and bounded evidence references `{ acceptedResultId, ordinal }` and `{ mediaDigest, startMs, endMs }`) (FR-001, FR-003, FR-014, FR-016, FR-017; research R3, R8, R9)
- [x] T004 [P] Split the fact reads in `src/platform/SocAlytics.Platform.Application/Analysis/Facts/`: add `IRunFactStreamReader.cs` and `IRunFactReader.cs` with internal implementations that take an already-authorized run scope and contain all selection validation, ordering, pagination, continuation, coverage, and provenance logic, and make `ListRunFactStreams.cs` and `ListRunFacts.cs` member handlers that authorize through `IAccessAuthorizer` and call the cores, with unchanged member behavior (FR-010, FR-011; research R6)
- [x] T005 [P] Split the evidence media resolution in `src/platform/SocAlytics.Platform.Application/Analysis/Evidence/`: add `IEvidenceMediaResolver.cs` with an internal implementation that takes an already-authorized run scope and an audit actor and contains planning, segment states, materialization demand, integrity checks, and link issuance, and make `ResolveEvidenceMedia.cs` the member handler that authorizes and records the member grant audit through it, with unchanged member behavior (FR-012, FR-013; research R6)

### Tool-call abstractions, audit, and metrics

- [x] T007 Add in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Tools/`: `AgentToolOptions.cs` (`AgentOrchestration:Tools` with `MaxCallsPerAttempt` and `MaxClipResolutionsPerAttempt`, validated at start, no production defaults), `IAgentToolCallStore.cs` (reserve, count calls and clip resolutions per attempt, find reservations without a lineage entry), `ToolCallLineage.cs` (canonical-JSON `sha-256:` digests of arguments and of results "with every `media.url` and `media.expiresAt` removed", the `tool-call` lineage payload of data-model.md, and the append command that assigns the next sequence under the invocation lock and is idempotent per attempt and call identifier, so the first entry for a call stands and a later append is skipped), and `AgentToolAuditEvents.cs` (`agent.tool.call.refused` with reasons `caller-not-workload`, `attempt-stale`, `not-permitted`, `call-id-reused`, `limit-reached`; `agent.tool.call.scope-lost`; `agent.tool.evidence-media.granted` with media digests and latest expiry, never links); add `src/platform/SocAlytics.Platform.Application/Telemetry/AgentToolMetrics.cs` with the meter `SocAlytics.Platform.AgentTools` and the counter `agent_tool_calls_total` tagged `tool` and `outcome` (FR-007a, FR-007b, FR-016–FR-018b; research R9–R11)

### Store, binding, and tests of the foundation

- [x] T008 [P] Implement `src/platform/SocAlytics.Platform.Infrastructure/Persistence/AgentOrchestration/Tools/AgentToolCallStore.cs` with Dapper over `agent_tool_calls`, and add the new audit detail keys (tool, call identifier, initiating member, media digests, latest expiry; never links or payloads) to the allow-list in `src/platform/SocAlytics.Platform.Infrastructure/Persistence/PostgresAuditTrail.cs` (FR-007a, FR-007b, FR-018a; data-model.md)
- [x] T009 [P] Add `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Tools/AuthorizeToolCall.cs`: in one unit of work that locks the invocation row, check that the attempt is the invocation's current `active` attempt with the presented fencing token, an unexpired lease, and an unreached deadline (otherwise `agent-attempt-stale` with the orchestration's dispositions, audit only); re-evaluate the initiating member through `IInvocationScopeEvaluator` and on scope loss run the orchestration's scope-loss transition (attempt `fenced`, invocation `failed` with `scope-lost`, lineage `scope-lost`) plus a `tool-call` entry with outcome `scope-lost` and the `agent.tool.call.scope-lost` audit event; refuse a tool outside the invocation's pinned tool policy (`not-permitted`, lineage entry and audit, no fencing); refuse a reused call identifier (`call-id-reused`, audit only); refuse exhausted per-attempt limits (`limit-reached` with `calls` or `clip-resolutions`, lineage entry and audit); otherwise reserve the call through `IAgentToolCallStore` (FR-005–FR-009; research R4, R5, R10)
- [x] T010 [P] Add `src/platform/Tests/SocAlytics.Platform.Contracts.Tests/AgentToolAllowlistTests.cs`: `AgentToolAllowlist` equals `contracts/agent-tools/tool-allowlist/v1/examples/valid/allowlist-1.0.0.json`, and its tool identifiers equal the tool allowlist of `contracts/agent-catalog/catalog-baseline/v1/examples/valid/baseline-1.0.0.json` (FR-001; research R3)

### Foundation registration

- [x] T011 Register `AgentToolOptions`, `IAgentToolCallStore`, `AuthorizeToolCall`, `ToolCallLineage`, and `AgentToolMetrics` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs` and `src/platform/SocAlytics.Platform.Infrastructure/InfrastructureServiceCollectionExtensions.cs`, and add the development and test values (`MaxCallsPerAttempt` 50, `MaxClipResolutionsPerAttempt` 10) to `src/platform/SocAlytics.Platform.Api/appsettings.Development.json` and `src/platform/Tests/SocAlytics.Platform.Integration.Tests/IdentityAccess/Support/TestIdentityAccessSettings.cs` (FR-007b; research R10)

**Checkpoint**: Foundation ready — tool calls can be bound, authorized, reserved, and recorded

---

## Phase 2: User Story 1 - An agent reads the evidence of its conversation's match (Priority: P1) 🎯 MVP

**Goal**: The workload reads the match, bound run, fact streams, accepted facts, and evidence media of its invocation's bound run through `get_match` and `get_video_clip`.

**Independent Test**: The simulated agent workload claims an invocation bound to a completed run with indexed facts and calls `get_match` without and with a fact selection and `get_video_clip` for one returned fact; the answers equal the member reads for the same run and selection.

### Implementation for User Story 1

- [x] T012 [P] [US1] Add `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Tools/GetMatchTool.cs`: after `AuthorizeToolCall`, read the match summary, the bound run (`state`, `isCurrent`, `supersededBy` from the supersession reads), and the fact streams through `IRunFactStreamReader`; with a `facts` selection read a page through `IRunFactReader`; answer `unavailable` with `capability-not-in-run` for a capability, version, schema version, or node key the run does not contain; append the `tool-call` lineage entry with the returned fact references and count the call in `AgentToolMetrics` (FR-007, FR-010, FR-011, FR-015, FR-016; research R7)
- [x] T013 [P] [US1] Add `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Tools/GetVideoClipTool.cs`: after `AuthorizeToolCall`, resolve a fact (accepted result and position with optional context) or a range of the bound run through `IEvidenceMediaResolver` with the workload as audit actor, answer "not found" for a fact outside the bound run, record `agent.tool.evidence-media.granted` when a link is issued, append the lineage entry with media references and a result digest without links, map storage failure to outcome `failed` with `evidence-storage-unavailable`, and never store, log, or trace links (FR-007, FR-012, FR-013, FR-016, FR-017; research R6, R9)

### Endpoints for User Story 1

- [ ] T014 [US1] Add `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentToolEndpoints.cs` with `MapAgentToolApi()` (route group `/api/v1/agent-tools`, policy `AgentWorkload`, HTTPS required, tag `Agent tools`) and the operations `agentToolGetMatch` and `agentToolGetVideoClip` (`POST /api/v1/agent-tools/attempts/{attemptId}/get-match` and `/get-video-clip`, body `{ callId, fencingToken, arguments }`, response `{ callId, toolId, outcome, result }`, problems of `contracts/agent-tools-api.md` through `ProblemResults`); register `GetMatchTool` and `GetVideoClipTool` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`; map the group in `src/platform/SocAlytics.Platform.Api/Program.cs`; refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` with `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1 dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --filter OpenApiSnapshotTests` (never in the verification environment) and run the Host tests without the variable (FR-002, FR-004, FR-020; research R2)
- [ ] T015 [US1] Extend the integration-test workload support in `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Support/TestAgentWorkload.cs` with a typed tool client that calls any allowlisted operation with an attempt, fencing token, call identifier, and arguments over the DPoP-authenticated HTTP protocol (FR-021)

### Tests for User Story 1

- [ ] T016 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/GetMatchToolTests.cs`: overview equals the member fact-stream listing and match read for the bound run; fact selections equal the member fact read across at least three pages, with ranges, coverage, and provenance; a superseded bound run is still read and reported with `isCurrent` false; facts of another run of the same match never appear; a capability not in the run answers `unavailable`; invalid selections answer `400` (FR-007, FR-010, FR-011, FR-015; SC-001)
- [ ] T017 [P] [US1] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/GetVideoClipToolTests.cs`: fact and range resolutions equal the member evidence-media resolution (parts, states, offsets, shortened windows); a fact of another run answers "not found"; storage failure answers `503` with lineage outcome `failed`; issued links appear in no database row, lineage entry, audit event, or captured log; a grant audit event carries digests and expiry only (FR-012, FR-013, FR-017, FR-018a; SC-001, SC-004)

**Checkpoint**: User Story 1 is functional and testable on its own (MVP)

---

## Phase 3: User Story 2 - Tools are least-privilege, attempt-bound, and fail closed (Priority: P2)

**Goal**: Only the workload's current attempt may call the tools of its pinned tool policy; scope loss fences the attempt and denies every later call.

**Independent Test**: Calls by member sessions, Analyst Manager tokens, stale tokens, expired leases, and for tools outside the pinned policy are refused without evidence; removing the initiating member's Coach role during an attempt makes the next call fence the attempt and every later call stale.

### Implementation for User Story 2

- [ ] T018 [US2] Add `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentToolAuthorizationResultHandler.cs`, an `IAuthorizationMiddlewareResultHandler` that, for endpoints of the `/api/v1/agent-tools` group only, records `agent.tool.call.refused` with reason `caller-not-workload` when authentication or the `AgentWorkload` policy fails and then delegates to the default handler, and register it in `src/platform/SocAlytics.Platform.Api/Program.cs` (FR-004, FR-018a; research R11)

### Tests for User Story 2

- [ ] T019 [US2] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/ToolCallAuthorizationTests.cs`: member sessions, Analyst Manager tokens, and anonymous callers get `401` and an audit event; each stale disposition (`attempt-not-current`, `lease-expired`, `attempt-fenced`, `invocation-ended`) reads nothing and writes no lineage entry; a tool outside the pinned tool policy answers `403 agent-tool-not-permitted` with a lineage entry and audit event and no fencing; a reused call identifier answers `409 agent-tool-call-id-reused`; the 51st call and the 11th clip resolution answer `429 agent-tool-call-limit-reached` without fencing; removing the initiating member's Coach role, deactivating the member, or leaving only a Viewer role makes the next call answer `409 agent-attempt-stale` with `scope-lost`, fences the attempt, fails the invocation with `scope-lost` in the same transaction, and every later call is stale; concurrent tool calls of one attempt each get one lineage entry in recorded order; the telemetry counter counts by tool and outcome without arguments or results (FR-004–FR-009, FR-018a, FR-018b; SC-002, SC-003)

**Checkpoint**: User Stories 1 and 2 work independently

---

## Phase 4: User Story 3 - Every tool call is attributable in the lineage, and missing analysis is reported (Priority: P3)

**Goal**: Concept and player-statistics tools answer "analysis unavailable"; every tool call appears in the owner's lineage without evidence content; the simulated workload uses the tools.

**Independent Test**: An invocation whose attempt called `get_match`, a fact selection, `get_video_clip`, and `get_formations` shows four `tool-call` lineage entries in call order without payloads or links, and `get_formations` answered "analysis unavailable" naming `formation-analysis`.

### Implementation for User Story 3

- [ ] T020 [P] [US3] Add `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Tools/UnavailableAnalysisTool.cs` for `get_player_stats` and the eight concept tools: after `AuthorizeToolCall`, validate the optional `{ fromMs?, toMs? }` range like the fact read, read nothing, answer `unavailable` with reason `no-accepted-producer` and the producing capability from `AgentToolAllowlist`, and append the lineage entry (FR-003, FR-014; research R8)
- [ ] T021 [P] [US3] Close interrupted reservations in `src/platform/SocAlytics.Platform.Application/AgentOrchestration/Execution/MonitorAgentAttempts.cs`: for every `agent_tool_calls` reservation without a `tool-call` lineage entry whose attempt has ended or whose reservation is older than the attempt's maximum duration, append a `tool-call` entry with outcome `failed` and reason `interrupted` (FR-016; SC-004; research R5)
- [ ] T022 [P] [US3] Add the `tool-call` kind and its payload schema (`callId`, `toolId`, `outcome`, `reason`, `argumentsDigest`, `resultDigest`, `evidence`, `producingCapability`) to the lineage response of `getAgentInvocationLineage` in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentConversationEndpoints.cs`, keeping owner-only authorization, and refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T014 (FR-018, FR-020)

### Endpoints for User Story 3

- [ ] T023 [US3] Map the nine operations `agentToolGetPlayerStats`, `agentToolFindCounterAttacks`, `agentToolFindTurnovers`, `agentToolGetHeatmap`, `agentToolFindBallActions`, `agentToolFindSetPlays`, `agentToolGetFormations`, `agentToolFindPressingPhases`, and `agentToolGetTacticalPhases` in `src/platform/SocAlytics.Platform.Api/AgentOrchestration/AgentToolEndpoints.cs`, register `UnavailableAnalysisTool` in `src/platform/SocAlytics.Platform.Application/ApplicationServiceCollectionExtensions.cs`, and refresh the committed OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` as in T014 (FR-001–FR-003, FR-020)

### Simulated workload

- [ ] T024 [US3] Make the simulated agent workload in `src/platform/Tests/SocAlytics.Platform.SimulatedAgentWorkload/` call `get_match` and `get_formations` in every attempt, interpret the question markers `[simulate:tools-facts]` (a fact selection of the first stream with accepted facts), `[simulate:tools-clip]` (a clip of the first returned fact), and `[simulate:tools-denied]` (a tool outside the pinned policy), and list the tool outcomes and evidence references it used in its deterministic, simulated advice (FR-019; research R12)

### Tests for User Story 3

- [ ] T025 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/UnavailableAnalysisToolTests.cs`: each of the nine tools answers `unavailable` with `no-accepted-producer` and its producing capability (or `null` for `get_player_stats`) even when the bound run has indexed facts, reads no fact, and writes a lineage entry (FR-003, FR-014; SC-005)
- [ ] T026 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/ToolCallLineageTests.cs`: every answered, unavailable, not-found, not-permitted, invalid, limit-reached, scope-lost, and failed call has exactly one `tool-call` entry in recorded order with call identifier, tool, attempt, digests, evidence references, and outcome; result digests exclude links and are stable for equal results; no entry contains a fact payload, link, or media content; an interrupted reservation is closed by the monitor; the owner's lineage read shows the entries, other members get "not found", and reading the lineage calls no tool (FR-016–FR-018; SC-004)
- [ ] T027 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Architecture.Tests/AgentToolRouteGroupTests.cs`: the operations under `/api/v1/agent-tools` in the OpenAPI document equal the entries of `AgentToolAllowlist` (operation identifier, method, path), the group contains no other endpoint, every endpoint requires the `AgentWorkload` policy and no member or Analyst Manager policy, and no allowlisted operation identifier belongs to an administration, mutation, lifecycle, worker, registration, claim, lease, checkpoint, or completion operation (FR-001, FR-002; SC-005)
- [ ] T028 [P] [US3] Add `src/platform/Tests/SocAlytics.Platform.Integration.Tests/AgentOrchestration/Tools/SimulatedWorkloadToolTests.cs` hosting the simulated workload's runner in process: a question with `[simulate:tools-facts]` and `[simulate:tools-clip]` succeeds within 30 seconds, its lineage shows the `get_match`, fact selection, `get_video_clip`, and `get_formations` entries, and its advice names the tool outcomes as simulated; `[simulate:tools-denied]` records a `not-permitted` entry and the invocation still succeeds (FR-019, FR-021; SC-006)

**Checkpoint**: All user stories are independently functional

---

## Phase 5: Polish & Cross-Cutting Concerns

**Purpose**: Documentation and full verification

- [ ] T029 Make `docs/architecture/` describe the implemented state: re-read `docs/architecture/intelligence-and-agents.md` (MCP Tools, Authorization, Lineage, And Lifecycle), `intelligence-agent-catalog.md` (introduction, MCP Tool Policy), `platform-implementation.md` (Control Plane, Agent Runtime), `analyst-capability-catalog.md` (evidence clips paragraph), `analysts-models-and-hardware.md` (Capability-Scoped Result Access), `security-and-data-governance.md` (Audit Events, `CTL-007`, `THR-007`, `DAT-008`), `tenancy-and-technology.md` (AI), `overview.md`, `terminology-and-principles.md`, and `docs/architecture/README.md`, and correct every statement this feature makes false, including the "decided and not yet implemented", "planned", "future work", and "design inputs rather than deployed behavior" wording about MCP tools, evidence tools, and agent evidence access (the plan's Architecture Updates list examples, not the whole job), while keeping the MCP server, Hermes, concept producers, and production values explicitly planned or unresolved and the decision status of `CTL-007` and `THR-007` unchanged; update `AGENTS.md` "Current State" and `src/platform/README.md` so they name the agent evidence tools route group, the allowlist contract, and their development evidence (constitution Principle I)
- [ ] T030 Run the quickstart's automated checks: `dotnet restore src/platform/SocAlytics.Platform.slnx`, `dotnet build src/platform/SocAlytics.Platform.slnx --no-restore`, `dotnet test src/platform/SocAlytics.Platform.slnx --no-build`, `npm ci` and `npm run build` in `src/clients/web` (the Kiota client regenerates from the refreshed snapshot), and `node .github/scripts/check-markdown.mjs`; fix what fails within this feature's files (FR-021; quickstart.md)

---

## Dependencies & Execution Order

### Phase Dependencies

- **Foundational (Phase 1)**: starts once `specs/archive/20261017-204332-agent-orchestration-invocations`, `specs/archive/20261018-101517-agent-catalog-registry`, `specs/archive/20261017-175648-fact-indexing-queries`, `specs/archive/20261017-224326-evidence-clip-access`, and `specs/archive/20261017-183953-analysis-run-supersession` have merged; blocks every story.
- **User Story 1 (Phase 2)**: after Phase 1.
- **User Story 2 (Phase 3)**: after User Story 1 (its tests call `get_match` through the route group).
- **User Story 3 (Phase 4)**: after User Story 1 (route group, test support); independent of User Story 2.
- **Polish (Phase 5)**: after all stories.

### Within Phases

- Phase 1: T001–T005 run in parallel; T004 and T005 split the read cores and keep member behavior unchanged; the member handlers keep working through the existing registrations and are covered by the existing fact-read and evidence-media integration tests (T006 was removed as redundant); T007 adds the abstractions; T008, T009, and T010 run in parallel; T011 registers what they created.
- Phase 2: T012 and T013 run in parallel; T014 maps them and refreshes the snapshot; T015 adds the test client; T016 and T017 run in parallel.
- Phase 3: T018, then T019.
- Phase 4: T020–T022 run in parallel; T023 maps the nine operations and refreshes the snapshot; T024 makes the simulated workload call the tools only after every tool it calls is mapped (it is hosted by existing orchestration tests); T025–T028 run in parallel.
- Phase 5: T029, then T030.

## Parallel Example: Phase 1

```text
Phase 1, group 1: T001 allowlist contract | T002 migration | T003 domain | T004 fact read cores | T005 evidence resolver core
Phase 1, group 2: T008 store and audit allow-list | T009 tool-call authorization | T010 allowlist contract tests
Phase 2, group 1: T012 get_match | T013 get_video_clip
Phase 2, group 2: T016 get_match tests | T017 get_video_clip tests
Phase 4, group 1: T020 unavailable tools | T021 interrupted reservations | T022 lineage read schema
Phase 4, group 2: T025 unavailable tests | T026 lineage tests | T027 route group tests | T028 simulated workload tests
```

## Implementation Strategy

### MVP First (User Story 1)

Complete Phase 1 and Phase 2: agents can read the match, accepted facts, and evidence media of their bound run with attempt binding, policy, scope, and lineage already enforced by the foundation.

### Incremental Delivery

1. Phase 1 → foundation (contract, schema, shared read cores, binding, reservation, lineage, audit, metrics).
2. User Story 1 → `get_match` and `get_video_clip` (MVP).
3. User Story 2 → caller audit and the fail-closed evidence.
4. User Story 3 → unavailable concept tools, interrupted reservations, lineage read, the nine operations, simulated workload, route-group guard.
5. Polish → architecture and repository documentation, full verification.

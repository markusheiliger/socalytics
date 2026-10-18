# Research: Agent Catalog Registry

## R1 — Where the catalog lives

- **Decision**: A `Catalog` sub-folder of the Agent Orchestration functional area in each layer (Domain, Application,
  Infrastructure, Api), with its own stores, handlers, and endpoints file.
- **Rationale**: The architecture lists Agent Orchestration as the owner of agent invocations and lineage and names no
  separate catalog area; submission and claim must read enablement and pins in the same unit of work. The platform is
  one layered monolith whose functional areas are folders (`AGENTS.md`).
- **Alternatives considered**: A new `AgentCatalog` functional area (adds an area the architecture does not name and a
  cross-area dependency at submission); extending the Analyst `Registry` folder (mixes Analyst and agent concerns and
  their different authorization paths).

## R2 — Record identity, digest, and immutability

- **Decision**: Every record is a JSON document with `schemaVersion`, `kind`, `id`, and `version` (SemVer 2.0.0 without
  build metadata). The digest is `sha-256:<hex>` of the RFC 8785 canonical form computed by the existing
  `CanonicalJson`/`ContentDigest` over the whole document, so identifier and version are part of the digest. `(kind, id,
  version)` is unique; an equal digest replays (`200`), a different digest is `409 agent-catalog-version-exists`. Records
  are inserted once and never updated or deleted; the agent version's lifecycle lives in separate columns guarded by
  optimistic concurrency.
- **Rationale**: FR-003, FR-004; the Analyst Registry already computes profile digests the same way; one digest form
  (`sha-256:`) as `contracts/README.md` requires.
- **Alternatives considered**: Digest over content without identity (two records could share a digest, which makes a
  digest ambiguous as a reference); server-assigned integer versions as for Analyst profiles (the architecture requires
  semantic versions for profiles and dimension sets).

## R3 — Validation pipeline and problem responses

- **Decision**: Two stages: (1) structural validation through `IContractValidator` against the record kind's embedded
  `contracts/agent-catalog/<kind>/v1` schema (closed objects, patterns, bounds); (2) catalog rules in Domain
  (`Catalog/AgentCatalogRules`) that need other records: allowlist and baseline membership, referenced records exist
  with matching kind and group, dimension coverage and total, dimension-set major compatibility, modifier bounds,
  disclosure, and dimension-set minor-version stability. All violations of both stages are collected and returned in
  one `400 agent-catalog-validation-failed` problem with `violations: [{ code, pointer, detail }]` (JSON Pointer to the
  offending field). Stable codes include `schema-violation`, `unknown-tool`, `tool-outside-baseline`,
  `unknown-agent-id`, `group-mismatch`, `artifact-not-found`, `artifact-kind-mismatch`, `missing-dimension`,
  `unknown-dimension`, `weight-out-of-range`, `weights-total-not-100`, `rationale-missing`, `dimension-set-not-found`,
  `dimension-set-incompatible`, `dimension-set-ids-changed`, `modifier-bound-exceeded`, `modifier-movement-exceeded`,
  `context-category-not-permitted`, `profile-slot-missing`, `profile-slot-not-allowed`, `disclosure-missing`,
  `safety-rule-missing`, `superseded-profile-invalid`, `provider-not-permitted`.
- **Rationale**: FR-005 requires every violation named; schema-only validation cannot express cross-record rules;
  `IContractValidator` already validates Analyst manifests at runtime from embedded schemas.
- **Alternatives considered**: Returning the first violation only (fails FR-005); one schema with `oneOf` per kind
  (worse error locations).

## R4 — Catalog baseline and tool allowlist

- **Decision**: The catalog baseline 1.0.0 (nine agent identifiers with group, domain, display name, and permitted-tool
  baseline, and the eleven-tool allowlist `get_match`, `get_player_stats`, `find_counter_attacks`, `find_turnovers`,
  `get_heatmap`, `find_ball_actions`, `find_set_plays`, `get_formations`, `find_pressing_phases`,
  `get_tactical_phases`, `get_video_clip`) is Domain code extending the orchestration's `AgentCatalogIds`, mirrored by
  the valid fixture `contracts/agent-catalog/catalog-baseline/v1/examples/valid/baseline-1.0.0.json`; a test asserts that
  both are equal and match the architecture tables. It is read through `GET /api/v1/agent-catalog/baseline`, not
  registered. Tool identifiers are written without `()`.
- **Rationale**: The baseline changes only with the architecture (Catalog Rules: exactly three CAs and six SAs; MCP
  Tool Policy), so it must not be editable through the API; the fixture makes it machine-readable for Hermes and the
  Web UI.
- **Alternatives considered**: A registered baseline record (would let operators widen the least-privilege baseline
  without an architecture change); configuration (not versioned or validated).

## R5 — Artifact formats (Wave 0)

- **Decision** (all closed objects, bounded strings, `schemaVersion` `1.0.0`):
  - `prompt-template`: `group` (`coach`|`specialist`), `responseContract` (`agent-response/1`, the five sections of
    Prompt And Advice Governance), `text` (1–32,000 characters), `slots` (subset of `agent_id`, `display_name`,
    `philosophy_profile`, `tool_policy`, `evidence_context`; each placeholder `{{slot}}` occurs in `text` exactly once
    when declared and never when undeclared), and for `coach` `dimensionSetMajor` (integer) and the required
    `philosophy_profile` slot.
  - `model-selection`: `provider` (`openai`, `github-models`, `simulated`), `model` and `deployment` (pattern
    `^[A-Za-z0-9._/:-]{1,128}$`), `parameters` (`temperature` 0–2, `topP` 0–1, `maxOutputTokens` 1–32,768,
    `reasoningEffort` `low`|`medium`|`high`; all optional).
  - `tool-policy`: `tools` (1–11 unique allowlisted identifiers).
  - `safety-policy`: `group`, `routes` (subset of `user-to-coach`, `user-to-specialist`, `coach-to-specialist`, matching
    the group), `hardBoundaries` (1–20 strings up to 500 characters), `escalationCategories` and `refusalCategories`
    (enumerations: `medical`, `safeguarding`, `welfare`, `privacy`, `sensitive-traits`, `raw-data-access`,
    `unauthorized-data`, `autonomous-decision`), and `rules` (enumeration; `coach` requires `simulation-disclosure`,
    `non-impersonation`, `non-fabrication`).
  - `dimension-set`: `dimensions` (1–32 `{ id, meaning, acceptedEvidence: [tool ids] }`, unique kebab-case ids).
  - `philosophy-profile`: `dimensionSet { id, version }`, `weights` (object keyed by dimension id: `{ weight 0–100
    integer, rationale ≤1,000, provenance ≤1,000 }`), `author`, `reviewers { architecture: [..], footballDomain: [..] }`
    (each at least one), optional `supersedes { version }`.
  - `modifier-policy`: `dimensionSetMajor`, `perDimensionBound` (1–3), `maxPositiveMovement` (1–10),
    `contextCategories` (subset of `score-state`, `match-period`, `player-number-state`, `opponent-phase-behavior`,
    `evidence-availability`), `budgetNeutral: true` (const).
  - `agent-version`: `agentId`, `group`, `displayName`, `artifacts { promptTemplate, modelSelection, toolPolicy,
    safetyPolicy, philosophyProfile?, modifierPolicy? }` each `{ id, version, digest }`, and for `coach`
    `disclosure` (20–1,000 characters).
- **Rationale**: FR-007–FR-014; the slot rule makes "exactly one profile-rendering slot" testable; enumerations make
  secret-carrying fields impossible; bounds come from the catalog's prompt drafts (under 3,000 characters) with room for
  growth.
- **Alternatives considered**: Free-form safety policies (untestable); positional weight arrays (the architecture
  requires dimension-id keys).

## R6 — Dimension-set and profile compatibility

- **Decision**: A dimension-set version `M.n.p` may be registered only if every existing version with the same id and
  major `M` has exactly the same set of dimension ids (`dimension-set-ids-changed` otherwise). Profiles reference an
  exact dimension-set version and must cover exactly its ids. An agent version is compatible when the prompt's
  `dimensionSetMajor`, the profile's dimension-set major, and the modifier policy's `dimensionSetMajor` are equal.
- **Rationale**: Dimension Set and Rendered CA Prompt Context; FR-010, FR-014.
- **Alternatives considered**: Allowing compatible minor additions (contradicts "stable within a major version").

## R7 — Profile distance and differentiation acknowledgement

- **Decision**: Distance `sum(|a_i - b_i|) / 200`, rounded to two decimals for display and compared unrounded, is
  computed against profiles referenced by approved, non-retired Coach Agent versions whose dimension set has the same id
  and major; a profile's own other versions and the version itself are included except the identical record. Approval
  of a Coach Agent version with any distance below 0.10 requires `differentiationReviewAcknowledged: true`
  (`409 differentiation-review-required` otherwise), recorded in the audit event.
- **Rationale**: FR-012, FR-015; the architecture's provisional distances (0.25, 0.25, 0.31) become a test.
- **Alternatives considered**: Rejecting close profiles (the architecture says review, not rejection).

## R8 — Lifecycle and enablement

- **Decision**: `agent_versions.state` `draft` → `approved` → `retired`; approve and retire are `POST` operations
  conditional on the version's `ETag` (`IfMatchHeader`). `agent_enablements` has one row per baseline agent identifier,
  inserted by the migration with no enabled version, so `PUT /api/v1/agent-catalog/enablements/{agentId}` with
  `If-Match` and `{ version | null }` always has a current row. Enabling requires an approved Specialist Agent version
  of that agent; Coach Agent versions get `409 coach-agent-enablement-unsupported`; a `simulated` model selection
  outside Development and Test gets `409 agent-version-not-enableable` (`reason: simulated-provider`). Retiring the
  enabled version gets `409 agent-version-enabled`. Self-approval is allowed and marked.
- **Rationale**: FR-015–FR-017; mirrors the Analyst Registry (approve, retire, default workflow) with the repository's
  conditional-request rules.
- **Alternatives considered**: Inserting the enablement row on first enable (no `ETag` to condition on).

## R9 — Replacing configured enablement

- **Decision**: Remove `AgentOrchestration:Agents:Enabled` from `AgentOrchestrationOptions` and
  `appsettings.Development.json`; `ListAvailableAgents`, `AddAgentConversationParticipant`, and `SubmitAgentInvocation`
  read `agent_enablements`. A leftover `Agents:Enabled` value is ignored and logged once at start in Development and
  Test. The orchestration integration fixture obtains enabled agents from the seed (or registers and enables them).
- **Rationale**: FR-018, SC-001.
- **Alternatives considered**: Keeping configuration as an intersecting filter (two sources of truth).

## R10 — Pinning, envelope, completion, and unpinned invocations

- **Decision**: In the submission unit of work, `SubmitAgentInvocation` reads the agent's enablement row and its
  agent version (row lock `FOR SHARE` on the enablement), stores `agent_version`, `agent_version_digest` on
  `agent_invocations`, and inserts one `agent_invocation_pins` row per artifact (kind, id, version, digest). The claim
  adds `agent { agentId, version, digest, artifacts: [{ kind, id, version, digest }] }` to the envelope; the
  `execution-envelope` contract becomes 1.1.0 with `agent` as an optional property (outputs may gain properties within
  a major; the platform always sets it, tests assert it) and `releases/1.1.0.schema.json`. A claim of an invocation
  without a pin answers `409 claim-obsolete`, ends the invocation `failed` with reason `agent-not-available`, and audits
  it. Completion compares `executionReferences.promptRef`, `toolPolicyRef`, and `safetyPolicyRef` with the pinned
  digests and refuses a mismatch with `409 execution-references-mismatch` (the attempt stays active); the lineage
  `completed` entry and accepted advice store the pinned references. The owner's invocation and lineage reads add
  `agent { version, digest, artifacts }`.
- **Rationale**: FR-019–FR-023b; contracts-and-compatibility allows additive changes within a major; the envelope is
  produced only by the platform.
- **Alternatives considered**: `execution-envelope/v2` (needless break for an additive output); a jsonb pin column
  (the child table keeps one row per reference and makes reads uniform).

## R11 — Attempt-scoped artifact read

- **Decision**: `POST /api/v1/agent-orchestration/attempts/{attemptId}/pinned-artifacts` with `{ fencingToken }`,
  policy `AgentWorkload`, returns `{ agentVersion: { record }, artifacts: [{ kind, id, version, digest, record }] }`
  with the stored documents (and for a Coach Agent version the referenced dimension set). It applies the same current
  attempt, token, lease, deadline, and scope checks as lease renewal and answers `409 agent-attempt-stale` with its
  disposition otherwise; it never accepts a digest from the caller, so no unpinned artifact can be requested. It is a
  read and records no operation.
- **Rationale**: FR-022; least privilege; `POST` keeps the fencing token out of URLs and logs, like the lease renewal.
- **Alternatives considered**: `GET` by digest for any workload (exposes every record and ignores the attempt).

## R12 — Audit, telemetry, and authorization

- **Decision**: Audit event types `agent.catalog.record.registered` (outcome accepted, replayed, refused with violation
  codes), `agent.catalog.version.approved` (self-approval, differentiation acknowledgement),
  `agent.catalog.version.retired`, `agent.catalog.enablement.changed` (previous and new version), and
  `agent.catalog.access.refused`; details hold kind, id, version, digest, agent id only. Meter `AgentCatalogMetrics`
  with counters `agent_catalog.registrations` (tags kind, outcome), `agent_catalog.approvals`,
  `agent_catalog.retirements`, `agent_catalog.enablement_changes`. All member catalog operations require Club Admin
  (`IAccessAuthorizer` club-level check) except `listAvailableAgents`, which keeps its orchestration rule.
- **Rationale**: FR-024–FR-025a; the audit detail allow-list in `PostgresAuditTrail` keeps text out.
- **Alternatives considered**: Reusing the Registry's audit event names (different area and identifiers).

## R13 — Development and Test seed

- **Decision**: `AgentCatalogSeeder` (hosted at API start like `ConfiguredWorkflowSeeder`) runs only in Development
  and Test   and only when `agent_catalog_artifacts` is empty. It registers, through the same validator and stores as the
    registration handlers but without member authorization (actor `system:seed`, audited), the prompt templates of `specialist-tactics` and `specialist-video-analysis` copied verbatim from
  First-Draft System Prompts (no slots), one `simulated` model selection (`model: simulated`, `deployment: simulated`),
  tool policies equal to the two baselines, and safety policies with their hard boundaries from the Specialist Agent
  Catalog, then registers version `0.1.0` of both agent versions, approves, and enables them. No Coach Agent artifact is
  seeded.
- **Rationale**: FR-026; the orchestration's local AppHost flow and Web UI tests need enabled specialists.
- **Alternatives considered**: A migration-time seed (would run in every environment).

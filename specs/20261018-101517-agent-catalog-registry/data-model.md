# Data Model: Agent Catalog Registry

All tables live in the `socalytics` schema and are created by one forward-only migration
`NNNN_agentorchestration_agent_catalog.sql`, numbered at implementation time as the next free number on an up-to-date
default branch.

## Tables

### `agent_catalog_artifacts` (immutable; `PersistedTableClassifications`: `Immutable`)

| Column | Type | Notes |
| --- | --- | --- |
| `kind` | text | `prompt-template`, `model-selection`, `tool-policy`, `safety-policy`, `dimension-set`, `philosophy-profile`, `modifier-policy` (check constraint). |
| `artifact_id` | text | Kebab-case identifier, 3–100 characters. |
| `artifact_version` | text | SemVer 2.0.0 without build metadata. |
| `version_major` | integer | Major of `artifact_version`, for dimension-set compatibility queries. |
| `digest` | text | `sha-256:<64 hex>` of the canonical document; unique. |
| `agent_group` | text | `coach`, `specialist`, or null for kinds without a group. |
| `document` | jsonb | The validated record exactly as canonicalized. |
| `registered_by` | text | Member id or `system:seed`. |
| `registered_at` | timestamptz | |

Primary key `(kind, artifact_id, artifact_version)`. Updates and deletes are rejected by a trigger.

### `agent_versions` (versioned: `CALL socalytics.attach_version_trigger('socalytics.agent_versions')`)

| Column | Type | Notes |
| --- | --- | --- |
| `agent_id` | text | One of the nine catalog baseline identifiers (check constraint). |
| `agent_version` | text | SemVer. |
| `agent_group` | text | `coach` or `specialist`; must match the baseline. |
| `digest` | text | Canonical document digest; unique. |
| `document` | jsonb | The validated agent-version record. |
| `state` | text | `draft`, `approved`, `retired`. |
| `registered_by`, `registered_at` | text, timestamptz | |
| `approved_by`, `approved_at`, `self_approval`, `differentiation_acknowledged` | text, timestamptz, boolean, boolean | Null until approved. |
| `retired_by`, `retired_at` | text, timestamptz | Null until retired. |
| `version` | bigint | Optimistic concurrency (`ETag`). |

Primary key `(agent_id, agent_version)`. A trigger rejects changes to `agent_id`, `agent_version`, `agent_group`,
`digest`, `document`, `registered_by`, and `registered_at`, and deletes.

### `agent_enablements` (versioned)

| Column | Type | Notes |
| --- | --- | --- |
| `agent_id` | text | Primary key; the migration inserts one row per catalog baseline identifier. |
| `agent_version` | text | Null when disabled; foreign key with `agent_id` to `agent_versions`. |
| `changed_by`, `changed_at` | text, timestamptz | Null until first change. |
| `version` | bigint | Optimistic concurrency (`ETag`). |

### `agent_invocations` (existing, from Agent Orchestration Direct Invocations)

Gains `agent_version` (text, null) and `agent_version_digest` (text, null). Null for invocations submitted before this
feature (unpinned). Its `failure_reason` check additionally allows `agent-not-available`.

### `agent_invocation_pins` (immutable; `PersistedTableClassifications`: `Immutable`)

| Column | Type | Notes |
| --- | --- | --- |
| `invocation_id` | uuid | Foreign key to `agent_invocations`. |
| `kind` | text | Artifact kind. |
| `artifact_id`, `artifact_version`, `digest` | text | Copied from the agent version's `artifacts`. |

Primary key `(invocation_id, kind)`. Inserted in the submission unit of work; never updated.

## Records (contract documents)

Every record has `schemaVersion` (`1.0.0`), `kind`, `id`, and `version`; the per-kind fields are in
[research.md R5](research.md#r5--artifact-formats-wave-0) and the contracts under `contracts/agent-catalog/`.

## Validation rules (Domain `Catalog/AgentCatalogRules`)

- Tool identifiers in tool policies and dimension evidence ∈ allowlist; tool policy ⊆ baseline of the agent version's
  agent identifier (Coach Agents: the "All CAs" row).
- Referenced artifacts exist by `(kind, id, version)` with the stated digest, and their `group` equals the agent
  version's group; Specialist Agent versions reference no profile or modifier policy; Coach Agent versions reference
  both and carry `disclosure`.
- Profile weights: keys equal the referenced dimension set's ids; integers in `[0,100]`; sum 100; rationale and
  provenance non-empty.
- Dimension sets: unique ids; same id set as every registered version with the same identifier and major.
- Modifier policies: `perDimensionBound` ≤ 3, `maxPositiveMovement` ≤ 10, permitted categories only.
- Compatibility: prompt `dimensionSetMajor` = profile dimension-set major = modifier `dimensionSetMajor`.
- Prompt slots: every declared slot occurs exactly once as `{{slot}}`, no undeclared slot occurs; coach prompts declare
  `philosophy_profile`, specialist prompts do not.
- Coach safety policies include `simulation-disclosure`, `non-impersonation`, and `non-fabrication`.
- Superseded profile: same profile id, lower existing version.

## State transitions

```text
agent version:  draft --approve--> approved --retire--> retired
                (retire refused while enabled; approved and retired documents never change)
enablement:     none <--enable/disable--> approved Specialist Agent version (one per agent id)
invocation pin: written once at submission; read by claim, artifact read, completion, and owner reads
```

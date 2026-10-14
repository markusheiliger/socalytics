# Data Model: Analyst Registry

Schema `registry`. Migration `NNNN_registry_analyst_registry.sql` takes the
next free number at implementation time.

## `analyst_capability_declaration` (immutable)

| Column | Type | Rules |
| --- | --- | --- |
| `image_digest` | text PK | `sha256:<hex>` of the registered image (index digest when an index was registered) |
| `repository` | text | Within the configured host and prefix (`seed` for development seeds) |
| `manifest_digest` | text | SHA-256 of `analyst-manifest.json` |
| `capability_id`, `capability_version` | text | From the manifest; indexed together |
| `tier`, `execution_scope` | text | `low-level` or `high-level`; `segment` or `match` |
| `model_digest` | text | From the manifest and labels |
| `manifest` | jsonb | Full validated manifest |
| `source` | text | `image` or `seed` |
| `registered_at`, `registered_by` | timestamptz, uuid | |

A trigger refuses `UPDATE` and `DELETE`. There is no `version` column, so the
table is classified in `PersistedTableClassifications.cs`.

## `analyst_profile`

| Column | Type | Rules |
| --- | --- | --- |
| `profile_id` + `profile_version` | text + int, PK | |
| `profile_digest` | text unique | RFC 8785 SHA-256 of the content (research.md § R4) |
| `image_digest` | text FK → declaration | |
| `capability_id`, `capability_version`, `model_digest` | text | Must equal the declaration |
| `content` | jsonb | Requirements, video policy, schema versions |
| `state` | text | `draft`, `approved`, or `retired` |
| `created_at`/`by`, `approved_at`/`by`, `retired_at`/`by` | | |
| `version` | bigint | `attach_version_trigger` |

A trigger refuses content changes unless `state = 'draft'` and refuses other
transitions than draft→approved, draft→retired, and approved→retired.

## `workflow_definition` (immutable)

| Column | Type | Rules |
| --- | --- | --- |
| `workflow_id` + `workflow_version` | text + text, PK | |
| `definition_digest` | text unique | RFC 8785 SHA-256 |
| `definition` | jsonb | Requested capabilities, completion policy, segmentation policy, nodes (node key → profile ID, version, and digest), edges (kind, predicate) |
| `created_at`, `created_by` | | |

A trigger refuses `UPDATE` and `DELETE`, and the table is classified in
`PersistedTableClassifications.cs`.

## `default_workflow` (single row)

| Column | Type | Rules |
| --- | --- | --- |
| `singleton` | boolean PK, check `true` | |
| `workflow_id`, `workflow_version` | text FK → workflow_definition | |
| `changed_at`, `changed_by` | | |
| `version` | bigint | `attach_version_trigger`; changed with `If-Match` |

## Profile state machine

```mermaid
stateDiagram-v2
    [*] --> Draft: Create (content validated against declaration)
    Draft --> Approved: Approve (audited; self-approval marked)
    Draft --> Retired: Retire
    Approved --> Retired: Retire (refused while the default workflow uses it)
```

## Resolution

`default_workflow` → `workflow_definition` → each node's approved profile and
its declaration → `ResolvedWorkflowNode` (the same fields as today) →
`ResolvedWorkflowDefinition`.

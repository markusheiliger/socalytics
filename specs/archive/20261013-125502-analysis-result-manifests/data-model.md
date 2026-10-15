# Data Model: Analysis Result Manifests and Validated Acceptance

## Result location (derived)

`results/{runId}/{logicalJobId}/{attemptId}/` — one per attempt; never shared.
Object names: `^[a-z0-9][a-z0-9._-]{0,127}$`; the manifest is `manifest.json`.

## Result object declaration (new table `analysis_result_object_declarations`, immutable)

| Column | Type | Rule |
| --- | --- | --- |
| `attempt_id` | uuid | FK to the attempt; part of the primary key |
| `name` | text | Object name rule above; part of the primary key |
| `size_bytes` | bigint | 1 ≤ size ≤ `MaxObjectBytes` (`MaxManifestBytes` for `manifest.json`) |
| `digest` | text | `sha-256:<64 lowercase hex>` |
| `declared_at` | timestamptz | First declaration |

Rules: per attempt at most `MaxObjectsPerAttempt` names and `MaxAttemptBytes`
in total; a repeated name with the same digest is idempotent; a different digest
is refused (`result-object-redeclared`). No `version` column; classified as
immutable.

## Accepted result manifest (new table `analysis_accepted_result_manifests`, immutable, 1:1)

| Column | Type | Rule |
| --- | --- | --- |
| `accepted_result_id` | uuid | PK; FK to `analysis_accepted_results` |
| `contract_version` | text | Supported manifest contract version (`1.0.0`) |
| `output_type` | text | `foundational-facts` or `soccer-concepts` |
| `validity_start_ms`, `validity_end_ms` | bigint | Half-open, within the job's segment or match window |
| `fact_count` | integer | ≥ 0 |
| `artifacts` | jsonb | `[{ name, digest, sizeBytes, mediaType }]`, each declared and attested |
| `upstream_lineage` | jsonb | `[{ acceptedResultId, capability, schemaVersion, manifestChecksum }]`, equal to the job's recorded upstream |

The existing `analysis_accepted_results` row keeps the manifest reference and
checksum; it is unchanged.

## Result manifest (contract `contracts/analysis/result-manifest/v1/`)

See research.md § R2 for the field list; `additionalProperties: false`
throughout; no credentials, presigned URLs, or inline media.

## Completion outcome (existing, extended)

On a failed acceptance check, the existing completion outcome record stores
outcome `failed` with reason `result-<check>` (research.md § R3), and the
attempt follows the existing failed-attempt transition. Storage unavailability
records nothing.

## Configuration (`Analysis:Results`)

| Setting | Development and test value | Rule |
| --- | --- | --- |
| `GrantLifetime` | `00:10:00` | > 0, ≤ 1 hour; grants also never outlive the lease |
| `MaxObjectsPerAttempt` | `16` | ≥ 1 |
| `MaxObjectBytes` | `104857600` (100 MiB) | ≥ `MaxManifestBytes` |
| `MaxAttemptBytes` | `1073741824` (1 GiB) | ≥ `MaxObjectBytes` |
| `MaxManifestBytes` | `1048576` (1 MiB) | ≥ 1 KiB |

All are explicit non-production values, validated at start.

## Simulated Analyst result modes (Manager, Development and Test only)

`valid` (default), `scope-mismatch`, `provenance-mismatch`, `lineage-mismatch`,
`artifact-missing`, `validity-invalid`, `contract-invalid` — configured per
capability next to the existing simulated outcomes.

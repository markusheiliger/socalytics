# Data Model: Capability Fact Payload Schemas

No relational schema change and no migration. The new data lives in JSON documents that already exist.

## Fact Payload Schema (manifest `output.factPayloadSchemas`)

| Field | Rule |
| --- | --- |
| key (fact kind) | Matches the fact kind pattern of the fact-artifact contract; at most 64 entries |
| value | A `payloadSchema` of the restricted subset (see `contracts/payload-schema-subset.md`); root `type` is `object` |
| Immutable | Fixed with the declaration of the image digest; content digest = canonical-JSON fingerprint of the map |
| Absent or empty | The capability declares no payload schemas: acceptance behaves as before |

Invariants: valid against the manifest schema `1.2.0`; at most 16 KiB, depth 8, and 256 schema nodes per schema; every
`$ref` is `#/$defs/<name>` and resolves; no `$ref` cycle.

## Capability Declaration (`analyst_capability_declaration.manifest`)

Existing `jsonb` copy of the manifest; gains `output.factPayloadSchemas` and `manifestVersion` `1.2.0`. The registered
image read already returns the manifest, so the schemas are shown unchanged. The Domain `CapabilityDeclaration` record
gains `FactPayloadSchemas` (`IReadOnlyDictionary<string, JsonElement>`, empty by default).

## Node Snapshot (per run, `analysis_logical_jobs` and run snapshot JSON)

| Key | Presence | Content |
| --- | --- | --- |
| `factPayloadSchemas` | Only when the declaration has schemas | Map kind → schema, as declared |
| `factPayloadSchemasDigest` | Together with the map | `sha-256:<hex>` of the canonical map |

`ResolvedWorkflowNode` gains the pinned map and digest; `WorkflowSnapshotMapper` writes them; the verifier reads them
from the node snapshot JSON. Existing runs have neither key and are never validated retroactively.

## Payload Validation Failure (stored reason of a failed completion outcome)

| Part | Rule |
| --- | --- |
| code | `result-payload-invalid`, `result-payload-kind-undeclared`, or `result-payload-validation-bound` |
| locator | `kind`, zero-based `fact` position in result order, `count` of nonconforming facts, `at` JSON pointer of the first violation |
| Bound | At most 256 characters; never a payload value |

State transition: unchanged. A rejection closes the attempt as `Rejected` and the completion outcome as `failed`
with this reason; normal retry rules apply; no accepted result and no indexed fact exist.

## Options

`Analysis:Results:PayloadValidation:MaxDuration` (development value, default 5 seconds, 1 to 60 seconds).

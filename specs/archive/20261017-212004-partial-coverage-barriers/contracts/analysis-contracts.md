# Analysis Contract Changes

All changes are additive minor versions in the existing `v1` directories, following
[Versioning And Compatibility](../../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility):
the `1.0.0` baselines under `releases/` stay unchanged, a `releases/1.1.0.schema.json` copy is added for each schema,
`x-socalytics-version` becomes `1.1.0`, and `contracts/README.md` lists the new versions.

## `analysis/analyst-manifest` 1.1.0

- `manifestVersion`: `enum ["1.0.0", "1.1.0"]` (was `const "1.0.0"`).
- `upstream[].coverage` (optional):

  ```json
  {
    "type": "object",
    "additionalProperties": false,
    "required": ["mode"],
    "properties": {
      "mode": { "enum": ["partial"] },
      "minimumAcceptedFraction": { "type": "number", "exclusiveMinimum": 0, "maximum": 1 }
    }
  }
  ```

- Semantic rules enforced by the Registry (not expressible in the schema alone): `coverage` only on match-scoped
  capabilities and on `required` or `conditional` entries; otherwise refusal `manifest-invalid` with the validation error `partial-coverage`.
- New examples: `valid/match-partial-coverage.json`, `valid/match-partial-coverage-fraction.json`;
  `invalid/partial-coverage-zero-fraction.json`, `invalid/partial-coverage-unknown-mode.json` (with `expectations.json`
  entries). Semantic refusals are covered by Registry integration tests.

## `analysis/upstream-results` 1.1.0

- `upstream.coverage` (optional): `{ "mode": "complete" | "partial", "minimumAcceptedFraction"?: number }`
  (`additionalProperties: false`, same fraction range). The platform emits it on every page and sets
  `contractVersion` to `1.1.0`.
- `coverage[]` is unchanged; for partial barriers its entries come from the job's input snapshot.
- New example: `valid/match-partial-barrier-failed-segment.json`.

## `analysis/result-manifest` 1.1.0

- `inputCoverage` (optional, `maxItems: 64`):

  ```json
  {
    "type": "object",
    "additionalProperties": false,
    "required": ["capability", "schemaVersion", "units"],
    "properties": {
      "capability": { "$ref": "../../../common/v1/common.schema.json#/$defs/capabilityRef" },
      "schemaVersion": { "$ref": "../../../common/v1/common.schema.json#/$defs/semanticVersion" },
      "units": {
        "type": "array",
        "maxItems": 1024,
        "items": {
          "type": "object",
          "additionalProperties": false,
          "required": ["state"],
          "properties": {
            "segmentId": { "$ref": "../../../common/v1/common.schema.json#/$defs/identifier" },
            "match": { "const": true },
            "state": { "enum": ["accepted", "missing", "failed", "blocked", "skipped"] }
          },
          "oneOf": [{ "required": ["segmentId"] }, { "required": ["match"] }]
        }
      }
    }
  }
  ```

- Acceptance: the platform verifier and the Manager's contract check accept `contractVersion` `1.0.0` and `1.1.0`.
  The platform rejects with `result-coverage-mismatch` as described in [data-model.md](../data-model.md).
- New examples: `valid/match-result-partial-coverage.json`; `invalid/coverage-unknown-state.json`.

## Unchanged

`analyst-job`, `attempt-completion`, `result-grants`, `run-state-changed`, and all `analyst-manager` contracts.

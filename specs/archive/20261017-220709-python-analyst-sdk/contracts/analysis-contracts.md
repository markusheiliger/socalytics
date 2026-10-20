# Contract: Analysis Contract Changes

Both changes follow [Versioning And Compatibility](../../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility):
released copies stay immutable, new `releases/<version>.schema.json` copies are added, `x-socalytics-version` is
updated, and `contracts/README.md` lists the versions. Shared definitions come from
`contracts/common/v1/common.schema.json`.

## `analysis/analyst-job` 1.1.0 (additive)

```json
{
  "workflow": {
    "type": "object",
    "additionalProperties": false,
    "required": ["id", "version"],
    "properties": {
      "id": { "$ref": "../../../common/v1/common.schema.json#/$defs/key" },
      "version": { "$ref": "../../../common/v1/common.schema.json#/$defs/semanticVersion" }
    }
  },
  "segmentWindow": {
    "description": "Present only for segment-scoped jobs: the platform's segment identity and the half-open nominal window that result acceptance compares.",
    "type": "object",
    "additionalProperties": false,
    "required": ["segmentId", "startMs", "endMs"],
    "properties": {
      "segmentId": { "$ref": "../../../common/v1/common.schema.json#/$defs/identifier" },
      "startMs": { "type": "integer", "minimum": 0 },
      "endMs": { "type": "integer", "minimum": 1 }
    }
  }
}
```

- Both properties are optional; a rule `if executionScope = match then not required segmentWindow` is added to the
  existing `allOf`. `endMs > startMs` is guaranteed by the platform's construction and checked by platform tests.
- The platform emits `contractVersion` `1.1.0` with `workflow` on every new message and `segmentWindow` on every
  segment-scoped message.
- New examples: `valid/segment-job-with-window.json`, `valid/match-job-with-workflow.json`;
  `invalid/segment-window-on-match-job.json` (`not`), `invalid/segment-window-unknown-property.json`
  (`additionalProperties`), with `expectations.json` entries.

## `analysis/analyst-attempt` 1.0.0 (new)

Path `contracts/analysis/analyst-attempt/v1/analyst-attempt.schema.json`, `$id`
`https://socalytics.invalid/contracts/analysis/analyst-attempt/v1/analyst-attempt.schema.json`, released copy
`releases/1.0.0.schema.json`, index row (owner: Analyst Manager; consumer: Analyst SDK; validation: platform contract
tests, Manager tests, SDK tests).

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["contractVersion", "attemptId", "attemptNumber"],
  "properties": {
    "contractVersion": { "$ref": "../../../common/v1/common.schema.json#/$defs/contractVersionV1" },
    "attemptId": { "$ref": "../../../common/v1/common.schema.json#/$defs/identifier" },
    "attemptNumber": { "$ref": "../../../common/v1/common.schema.json#/$defs/attemptNumber" }
  }
}
```

- Examples: `valid/attempt.json`; `invalid/missing-attempt-number.json` (`required`),
  `invalid/carries-fencing-token.json` (`additionalProperties`), `invalid/zero-attempt-number.json` (`minimum`).

## Unchanged

`result-manifest`, `upstream-results`, `fact-artifact` (versions from their own features), `attempt-completion`,
`result-grants`, `run-state-changed`, `analyst-manifest`, and all `analyst-manager` contracts.

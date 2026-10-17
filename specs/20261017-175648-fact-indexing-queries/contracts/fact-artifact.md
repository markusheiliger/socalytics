# Contract: Fact Artifact v1

Canonical artifact (created at implementation):
`contracts/analysis/fact-artifact/v1/fact-artifact.schema.json`, `$id`
`https://socalytics.invalid/contracts/analysis/fact-artifact/v1/fact-artifact.schema.json`,
`x-socalytics-version` `1.0.0`, released copy `releases/1.0.0.schema.json`,
examples under `examples/valid/` and `examples/invalid/` with an
`expectations.json`, and a row in `contracts/README.md` (owner: Scheduler and
Job Registry for the envelope; Analyst capability owners for payload
semantics). Shared definitions come from `contracts/common/v1/common.schema.json`.

## Shape

```json
{
  "contractVersion": "1.0.0",
  "capability": { "id": "player-detection", "version": "1.0.0" },
  "schemaVersion": "1.0.0",
  "outputType": "foundational-facts",
  "facts": [
    {
      "kind": "player-detected",
      "startMs": 120000,
      "endMs": 120040,
      "confidence": 0.93,
      "payload": { "trackId": "t-7", "box": [0.41, 0.22, 0.05, 0.12] }
    }
  ]
}
```

## Rules

- Required at the top level: `contractVersion`, `capability`, `schemaVersion`,
  `outputType`, `facts`; `additionalProperties: false`.
- `capability`: common `capabilityRef`. `schemaVersion`: common
  `semanticVersion`. `outputType`: `foundational-facts` or `soccer-concepts`.
- Each fact requires `kind` (common `key`), `startMs` (integer ≥ 0), `endMs`
  (integer ≥ 1), `confidence` (number 0..1), and `payload` (object, open);
  `additionalProperties: false` on the fact. `endMs > startMs` is checked by
  the platform (JSON Schema cannot compare fields).
- No URLs, credentials, or inline media are envelope fields; the payload is
  capability-owned and opaque to the platform.
- The media type that marks a fact artifact in the result manifest is
  `application/vnd.socalytics.facts+json`.

## Examples

| Example | Outcome |
| --- | --- |
| `valid/segment-foundational-facts.json` | valid |
| `valid/match-soccer-concepts.json` | valid |
| `valid/empty-facts.json` | valid (`facts: []`) |
| `invalid/missing-capability.json` | invalid (required) |
| `invalid/confidence-above-one.json` | invalid (maximum) |
| `invalid/negative-start.json` | invalid (minimum) |
| `invalid/payload-not-object.json` | invalid (type) |
| `invalid/unknown-fact-property.json` | invalid (additionalProperties) |
| `invalid/unknown-output-type.json` | invalid (enum) |

Platform-only rules (not expressible in the schema) are covered by integration
tests: `endMs > startMs`, range inside the validity window, header equal to the
manifest and node snapshot, fact count, and size limits.

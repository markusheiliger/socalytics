# Contract: Segment Manifest and Analysis Contract Changes

All changes follow [Versioning And Compatibility](../../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility):
released copies stay immutable, new `releases/<version>.schema.json` copies are added, `x-socalytics-version` is set,
and `contracts/README.md` has one index row per schema. Shared definitions (`identifier`, `sha256Digest`,
`contractVersionV1`, UTC timestamps) come from `contracts/common/v1/common.schema.json`.

## `segments/segment-manifest` 1.0.0 (new)

Path `contracts/segments/segment-manifest/v1/segment-manifest.schema.json`, `$id`
`https://socalytics.invalid/contracts/segments/segment-manifest/v1/segment-manifest.schema.json`; owner: Segment
Service (control-plane deployment); consumers: Analyst Manager, Analyst SDK; validation: platform contract tests,
Manager tests, SDK tests.

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["contractVersion", "identity", "segmentationPolicy", "nominalWindow", "requestedSourceInterval",
               "encodedInterval", "timelineSpans", "media", "producer", "createdAt"],
  "properties": {
    "contractVersion": { "$ref": "common#/$defs/contractVersionV1" },
    "identity": {
      "type": "object", "additionalProperties": false,
      "required": ["recordingVersionId", "timelineMappingDigest", "segmentationPolicyDigest", "segmentNumber"],
      "properties": {
        "recordingVersionId": { "$ref": "common#/$defs/identifier" },
        "timelineMappingDigest": { "$ref": "common#/$defs/sha256Digest" },
        "segmentationPolicyDigest": { "$ref": "common#/$defs/sha256Digest" },
        "segmentNumber": { "type": "integer", "minimum": 1 }
      }
    },
    "segmentationPolicy": {
      "type": "object", "additionalProperties": false, "required": ["version", "durationSeconds"],
      "properties": { "version": { "const": 1 }, "durationSeconds": { "type": "integer", "minimum": 1 } }
    },
    "nominalWindow": { "$ref": "#/$defs/halfOpenMs" },
    "requestedSourceInterval": { "$ref": "#/$defs/halfOpenMs" },
    "encodedInterval": { "$ref": "#/$defs/halfOpenMs" },
    "timelineSpans": {
      "type": "array", "minItems": 1,
      "items": {
        "type": "object", "additionalProperties": false,
        "required": ["mediaStartMs", "mediaEndMs", "matchStartMs"],
        "properties": {
          "mediaStartMs": { "type": "integer", "minimum": 0 },
          "mediaEndMs": { "type": "integer", "minimum": 1 },
          "matchStartMs": { "type": "integer", "minimum": 0 }
        }
      }
    },
    "media": {
      "type": "object", "additionalProperties": false,
      "required": ["artifactReference", "mediaType", "digest", "sizeBytes"],
      "properties": {
        "artifactReference": { "type": "string", "minLength": 1, "maxLength": 512 },
        "mediaType": { "const": "video/mp4" },
        "digest": { "$ref": "common#/$defs/sha256Digest" },
        "sizeBytes": { "type": "integer", "minimum": 1 }
      }
    },
    "producer": {
      "type": "object", "additionalProperties": false, "required": ["toolchain"],
      "properties": { "toolchain": { "type": "string", "minLength": 1, "maxLength": 200 } }
    },
    "createdAt": { "$ref": "common#/$defs/utcTimestamp" }
  },
  "$defs": {
    "halfOpenMs": {
      "type": "object", "additionalProperties": false, "required": ["startMs", "endMs"],
      "properties": { "startMs": { "type": "integer", "minimum": 0 }, "endMs": { "type": "integer", "minimum": 1 } }
    }
  }
}
```

`common` abbreviates `../../../common/v1/common.schema.json`; definition names follow the common schema at
implementation time. Rules the schema cannot express (`endMs > startMs`; encoded interval covers the requested
interval; nominal window equals `[(n-1)·D, n·D)`; spans ordered, non-overlapping, within the nominal window in match
time) are checked by the platform tests on every example and by the producer.

Examples: `valid/segment-manifest.json`, `valid/clipped-segment-manifest.json` (partial coverage, two spans);
`invalid/missing-media-digest.json` (`required`), `invalid/plain-digest-form.json` (`pattern`),
`invalid/zero-segment-number.json` (`minimum`), `invalid/unknown-property.json` (`additionalProperties`),
`invalid/audio-media-type.json` (`const`), each with `expectations.json` entries.

## `analysis/segment-media` 1.0.0 (new)

Path `contracts/analysis/segment-media/v1/segment-media.schema.json`; owner: Scheduler and Job Registry; consumer:
Analyst Manager. The response of `getAnalysisSegmentMedia` ([segment-media-read.md](segment-media-read.md)).

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["contractVersion", "attemptId", "logicalJobId", "manifestChecksum", "manifest", "grants"],
  "properties": {
    "contractVersion": { "$ref": "common#/$defs/contractVersionV1" },
    "attemptId": { "$ref": "common#/$defs/identifier" },
    "logicalJobId": { "$ref": "common#/$defs/identifier" },
    "manifestChecksum": { "$ref": "common#/$defs/sha256Digest" },
    "manifest": { "$ref": "../../../segments/segment-manifest/v1/segment-manifest.schema.json" },
    "grants": {
      "type": "array", "minItems": 2, "maxItems": 2,
      "items": {
        "type": "object", "additionalProperties": false, "required": ["name", "url", "expiresAt"],
        "properties": {
          "name": { "enum": ["segment-manifest.json", "segment.mp4"] },
          "url": { "type": "string", "format": "uri" },
          "expiresAt": { "$ref": "common#/$defs/utcTimestamp" }
        }
      }
    }
  }
}
```

Examples: `valid/segment-media.json`; `invalid/missing-grants.json`, `invalid/write-grant-name.json` (`enum`),
`invalid/single-grant.json` (`minItems`).

## `analysis/analyst-job` 1.2.0 (additive, after 1.1.0 of the Python Analyst SDK feature)

```json
{
  "segmentMedia": {
    "description": "Present only for segment-scoped jobs that became ready with materialized segment media.",
    "type": "object",
    "additionalProperties": false,
    "required": ["videoPolicy", "manifestChecksum"],
    "properties": {
      "videoPolicy": { "enum": ["required", "optional"] },
      "manifestChecksum": { "$ref": "../../../common/v1/common.schema.json#/$defs/sha256Digest" }
    }
  }
}
```

- Optional; a rule `if executionScope = match then not required segmentMedia` joins the existing `allOf`.
- The platform emits `contractVersion` `1.2.0` for every new readiness occurrence; messages of 1.0.0 and 1.1.0 stay
  valid.
- Examples: `valid/segment-job-with-media.json`; `invalid/segment-media-on-match-job.json` (`not`),
  `invalid/segment-media-forbidden-policy.json` (`enum`).

## Unchanged

`result-manifest`, `upstream-results`, `fact-artifact`, `attempt-completion`, `result-grants`, `run-state-changed`,
`analyst-attempt`, `analyst-manifest`, and the `analyst-manager` contracts.

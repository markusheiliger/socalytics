# Contract: Analyst Manifest and Image Packaging

## Analyst manifest (JSON Schema 2020-12)

Location: `contracts/analysis/analyst-manifest/v1/analyst-manifest.schema.json`,
released as `releases/1.0.0.schema.json`, with `examples/valid/` and
`examples/invalid/`, plus an index row in `contracts/README.md`.

Required properties:

- `manifestVersion`: `1.0.0`.
- `capability`: `{ id, version, tier: "low-level" | "high-level", executionScope: "segment" | "match" }`.
- `upstream`: array of `{ capabilityId, capabilityVersion, schemaVersion, kind: "required" | "optional" | "conditional" }`.
- `videoAccess`: `required`, `optional`, or `forbidden`.
- `output`: `{ schemaVersion }`.
- `compatibleProfiles`: array of profile IDs.
- `model`: `{ digest }`.
- `runtime`: `{ constraints[], accelerators[] }`.
- `resources`: object of string values.
- `requirements`: `{ minMaxAttempts, minLeaseSeconds, minStaleTimeoutSeconds, minExecutionTimeoutSeconds }`.
- `determinism`: `{ deterministic: boolean, idempotent: boolean }`.

Unknown properties are refused (`additionalProperties: false`), so additive
changes take a new minor version.

## Image packaging convention (for Analyst authors)

1. `analyst-manifest.json` sits beside the Dockerfile.
2. The **last** Dockerfile instruction that creates a layer is
   `COPY analyst-manifest.json /opt/socalytics/analyst-manifest.json`, so the
   last layer contains only that file.
3. Labels, set at build time:
   - `io.socalytics.analyst-manifest.version`: the manifest's `manifestVersion`;
   - `io.socalytics.analyst-manifest.sha256`: the SHA-256 of the file;
   - `io.socalytics.capability.id` and `io.socalytics.capability.version`;
   - `io.socalytics.model.digest`.
4. Layer media types are gzip-compressed or uncompressed tar.

## Import refusals

| Code | When |
| --- | --- |
| `reference-not-digest` | The reference uses a tag |
| `reference-not-allowed` | Outside the configured registry host and repository prefix |
| `image-not-found` | The registry reports 404 |
| `registry-unavailable` | Network failure, 5xx, or authentication failure (retryable) |
| `digest-mismatch` | Any fetched object's SHA-256 differs from its reference |
| `manifest-missing` | The last layer does not contain only `opt/socalytics/analyst-manifest.json` |
| `manifest-invalid` | Schema validation failed (details listed) |
| `labels-mismatch` | A required label is missing or differs from the manifest |
| `unsupported-media-type` | The layer compression is not supported |

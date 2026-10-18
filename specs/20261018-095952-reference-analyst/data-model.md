# Data Model: Reference Analyst Container on the Python SDK

No database table, migration, API resource, or shared contract changes (FR-019). The entities are files of the
capability project, values it registers through existing operations, and the facts it writes.

## Capability Declaration (`analyst-manifest.json`)

Validated against `contracts/analysis/analyst-manifest/v1/` (released 1.0.0); values in
[research.md § R8](research.md#r8---manifest-labels-and-the-last-layer).

| Field | Value | Rule |
| --- | --- | --- |
| `capability.id` / `version` | `reference-media-probe` / `1.0.0` | development reference id (FR-001) |
| `capability.tier` / `executionScope` | `low-level` / `segment` | FR-003 |
| `upstream` | `[]` | no dependencies |
| `videoAccess` | `required` | FR-003 |
| `output.schemaVersion` | `1.0.0` | equals the fact artifact's `schemaVersion` |
| `compatibleProfiles` | `["reference-media-probe-dev"]` | the one profile tests and docs register |
| `model.digest` | `sha-256:` + 64 zeros | the platform's no-model digest |
| `runtime` | constraints `["linux-amd64"]`, accelerators `[]` | FR-012 |
| `resources` | `memory: 512Mi`, `cpu: 1` | fits the Docker runtime's test limits |
| `requirements` | 1 attempt, 30 s lease, 60 s stale, 300 s execution (minimums) | the profile may state larger values |
| `determinism` | deterministic and idempotent | FR-007 |

## Image Labels

| Label | Source |
| --- | --- |
| `io.socalytics.analyst-manifest.version` | manifest `manifestVersion` |
| `io.socalytics.analyst-manifest.sha256` | SHA-256 of the manifest file (verified during the build) |
| `io.socalytics.capability.id` / `.version` | manifest `capability` |
| `io.socalytics.model.digest` | manifest `model.digest` |

## Pinned Image Inputs

| Input | Pin | Verified by |
| --- | --- | --- |
| Base image | `python:3.12-slim-bookworm@sha256:<digest>` | the digest reference itself |
| uv | archive URL and SHA-256 equal to the environment's toolchain JSON | `fetch_verified.py` at build; project test |
| FFmpeg and ffprobe | archive URL and SHA-256 equal to the toolchain JSON | `fetch_verified.py` at build; project test |
| Python packages | `uv.lock` of the capability project (includes the SDK by path) | `uv sync --locked` |

## Analyst Profile (`reference-media-probe-dev`)

Created and approved through the existing Registry operations by the end-to-end test and the local procedure.

| Content field | Value |
| --- | --- |
| `capabilityId` / `capabilityVersion` | `reference-media-probe` / `1.0.0` |
| `modelDigest` | the no-model digest |
| `runtimeConstraints` / `accelerators` | `["linux-amd64"]` / `[]` |
| `resources` | `memory: 512Mi`, `cpu: 1` |
| `maxAttempts`, `leaseSeconds`, `staleTimeoutSeconds`, `executionTimeoutSeconds` | at least the manifest's requirements |
| `videoPolicy` | `required` |
| `inputSchemaVersions` / `outputSchemaVersion` | `{}` / `1.0.0` |

## Frame-Second Fact

One fact of the SDK-written `facts.json` (fact-artifact 1.0.0, output type `foundational-facts`).

| Field | Rule |
| --- | --- |
| `kind` | `frame-second-probe` |
| `startMs`, `endMs` | the match-second `[k·1000, (k+1)·1000)` clipped to the nominal window and the second's mapped part; `endMs > startMs` |
| `confidence` | `1.0` |
| `payload.decodedFrames` | integer ≥ 0: frames whose match time lies in `[k·1000, (k+1)·1000)`, inside the window, mapped, and inside the requested source interval |
| `payload.meanLuma` | present only when `decodedFrames > 0`: `min`, `mean`, `max` of the frames' average 8-bit luma, each in `[0, 255]`, rounded half-even to 3 decimals, `min ≤ mean ≤ max` |

Facts are ordered by `startMs`; no two facts share a second. The payload schema is the project-local
`schemas/frame-second-probe.payload.schema.json` ([contract](contracts/reference-media-probe.md#fact-payload)).

## Run-Time Files (unchanged interface)

| Path | Access | Use |
| --- | --- | --- |
| `/socalytics/job.json`, `/socalytics/attempt.json` | read-only | read through the SDK |
| `/socalytics/media/segment-manifest.json`, `segment.mp4` | read-only | verified by the SDK; decoded by ffprobe |
| `/socalytics/result/` | write | `facts.json` and `manifest.json`, written only by the SDK |
| `/socalytics/scratch/` | write | ffprobe working directory; nothing persisted |

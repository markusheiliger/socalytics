# Contract: Reference Media Probe Analyst

The component-local interface of `src/analysts/capabilities/reference-media-probe/`: how it is built, what it
declares, how it runs, and what it writes. Shared contracts (`analyst-manifest`, `analyst-job` 1.2.0,
`analyst-attempt`, `segment-manifest`, `result-manifest`, `fact-artifact`) are used unchanged.

## Build

| Item | Contract |
| --- | --- |
| Command | `docker build -f src/analysts/capabilities/reference-media-probe/Dockerfile -t <reference> .` from the repository root |
| Build arguments | none required; the `ARG` defaults are the pins and must not be overridden for registered images |
| Ignore file | `Dockerfile.dockerignore` next to the Dockerfile admits only the SDK and capability sources, manifests, and lockfiles |
| Builder compatibility | works with the classic builder and BuildKit (no `ADD --checksum`, no `RUN --mount`) |
| Platform | `linux/amd64` single-platform image |
| Failure | a differing archive hash, a manifest checksum differing from the label value, or a stale `uv.lock` fails the build with a message that names the input |
| Last layer | only `opt/socalytics/analyst-manifest.json` |

## Declaration

`analyst-manifest.json` as in [data-model.md](../data-model.md#capability-declaration-analyst-manifestjson); labels as
in [data-model.md](../data-model.md#image-labels). The Registry imports the image by digest without change.

## Run

| Property | Contract |
| --- | --- |
| Entry point | the SDK's `run` with `output_type="foundational-facts"` |
| User | any non-root uid and gid; image default `65532:65532` |
| Root file system | read-only; writes only to `/socalytics/result/` (through the SDK) and nothing else |
| Network, environment, credentials | none needed; no network connection and no configuration from environment variables (the image's `PATH` only locates its own tools) |
| Inputs | `job.json` (analyst-job 1.2.0 with `segmentMedia`), `attempt.json`, `/socalytics/media/` |
| Exit status | the SDK's: `0` result written, `2` invalid inputs (including `segment-media-required` when the job has no segment media), `1` other failures (including `media-decode-failed`) |
| Standard output and error | the SDK's single bounded lines; ffprobe's standard error is captured, bounded, and never echoed with media content |

## Fact Payload

Kind `frame-second-probe`; range and confidence rules in [data-model.md](../data-model.md#frame-second-fact). Payload
schema (`schemas/frame-second-probe.payload.schema.json`, JSON Schema 2020-12, owned and tested by the project):

```json
{
  "type": "object",
  "additionalProperties": false,
  "required": ["decodedFrames"],
  "properties": {
    "decodedFrames": { "type": "integer", "minimum": 0 },
    "meanLuma": {
      "type": "object",
      "additionalProperties": false,
      "required": ["min", "mean", "max"],
      "properties": {
        "min": { "type": "number", "minimum": 0, "maximum": 255 },
        "mean": { "type": "number", "minimum": 0, "maximum": 255 },
        "max": { "type": "number", "minimum": 0, "maximum": 255 }
      }
    }
  }
}
```

`meanLuma` is present exactly when `decodedFrames > 0`; `min ≤ mean ≤ max` is checked by the project tests.

Example fact:

```json
{ "kind": "frame-second-probe", "startMs": 5000, "endMs": 6000, "confidence": 1.0,
  "payload": { "decodedFrames": 25, "meanLuma": { "min": 101.236, "mean": 104.512, "max": 108.004 } } }
```

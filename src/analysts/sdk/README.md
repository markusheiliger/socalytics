# SocAlytics Analyst SDK

The internal Python library (distribution `socalytics-analyst-sdk`, package `socalytics_analyst`) that lets an Analyst
author implement the fixed Analyst container interface without handling the mounts, the contract validation, or the
exit behavior. It contains no capability, no model, no network client, and no platform credentials, and it is not
published: it is an independent Python project of this repository (CPython 3.12, `uv`).

The SDK validates the Logical Job, the attempt context, the staged upstream results, and its own output against copies
of the canonical JSON Schemas in `contracts/`, parses them into typed Pydantic models, and writes the fact artifact and
the result manifest atomically with correct digests, sizes, lineage, validity window, fact count, and coverage. The
platform validates the same schemas independently when it accepts a result.

## Container interface

The SDK reads and writes the mounts the Analyst Manager prepares for every attempt. Nothing else is available to the
Analyst: no network, no environment variables, no credentials.

| Path in the container | Access | Content |
| --- | --- | --- |
| `/socalytics/job.json` | read-only | The Logical Job (`analysis/analyst-job`) |
| `/socalytics/attempt.json` | read-only | The attempt context (`analysis/analyst-attempt`) |
| `/socalytics/inputs/` | read-only | Staged upstream results with `coverage.json` (`analysis/upstream-results`), digest-verified against their manifests on load |
| `/socalytics/media/` | read-only | Present only when the job carries `segmentMedia`: `segment-manifest.json` (`segments/segment-manifest`) and `segment.mp4`, verified on load and exposed as `context.media` (`manifest`, `path`, `to_match_ms`) |
| `/socalytics/result/` | read-write | `facts.json` and `manifest.json`, plus supporting artifacts (top-level files only) |
| `/socalytics/scratch/` | read-write | Working space, available as `context.scratch` |

Facts outside the half-open nominal window (`startMs` inclusive, `endMs` exclusive) are refused. `manifest.json` is
written last; after any failure the result mount holds no manifest.

When the Analyst's own manifest (`/opt/socalytics/analyst-manifest.json`, or the path in `SOCALYTICS_ANALYST_MANIFEST`;
manifest `1.2.0`) declares `output.factPayloadSchemas`, `add_fact` validates every payload against the schema of its kind
and always fails the write on a violation: `fact-payload-invalid` for a payload that violates its schema and
`fact-kind-undeclared` for a kind without a declared schema (both exit `1`, never carrying a payload value). Without a
declared schema, payloads are not checked by the SDK. The platform validates the same payloads again at acceptance, and
the shared cases `contracts/analysis/analyst-manifest/v1/examples/fact-payloads.json` give both the same verdicts. This is
development evidence; payloads are not interpreted.

## Minimal example

[`examples/minimal_analyst.py`](examples/minimal_analyst.py):

```python
from socalytics_analyst import AnalystContext, Fact, run


def analyse(context: AnalystContext) -> None:
    start, end = (0, 1000)
    if context.job.segment_window is not None:
        start, end = context.job.segment_window.start_ms, context.job.segment_window.end_ms
    context.result.add_fact(Fact(kind="example-seen", start_ms=start, end_ms=end, confidence=1.0, payload={}))
    context.result.set_confidence(1.0)
    if context.job.execution_scope == "match":
        context.result.set_validity_window(start, end)


if __name__ == "__main__":
    run(analyse, output_type="foundational-facts")
```

`run(analyst, *, output_type, root=Path("/socalytics"))` loads and verifies the inputs, calls the function with an
`AnalystContext` (`job`, `attempt`, `inputs`, `media`, `result`, `scratch`), finalizes the result, prints one summary line, and
exits. `output_type` is `foundational-facts` or `soccer-concepts`. `root` exists for tests only. The author calls
`context.result.add_fact`, `add_artifact`, `set_validity_window`, `set_confidence`, and `add_evidence_reference`;
`finalize` belongs to `run`. When at least one fact is added, `set_confidence` is required.

## Media access

`context.media` is `None` unless the job carries `segmentMedia`, which the platform sets only for nodes whose pinned
video policy provides media and whose segment was materialized. The Analyst Manager has already verified and staged the
files, and the SDK verifies them again on load against the job's `segmentMedia` and the segment manifest. `media.path`
is the read-only `segment.mp4` (the primary video stream, stream-copied, so it contains no audio), `media.manifest` is the typed
`segments/segment-manifest`, and `media.to_match_ms(media_ms)` maps a media time to match time through the manifest's
timeline spans (half-open; `None` outside every span). A job with an `optional` policy can run without media, so check
for `None`:

```python
from socalytics_analyst import AnalystContext, Fact, run


def analyse(context: AnalystContext) -> None:
    media = context.media
    if media is None:
        return
    match_ms = media.to_match_ms(0)
    if match_ms is not None:
        context.result.add_fact(Fact(kind="media-start", start_ms=match_ms, end_ms=match_ms + 1, confidence=1.0, payload={}))
        context.result.set_confidence(1.0)
    with media.path.open("rb") as video:
        video.read(16)


if __name__ == "__main__":
    run(analyse, output_type="foundational-facts")
```

## Exit statuses and error categories

| Exit status | Meaning |
| --- | --- |
| `0` | The result was finalized; standard output carries `socalytics-analyst: finalized facts=<n> artifacts=<n>` |
| `1` | The Analyst's own code failed, or a fact, artifact, or result value was refused |
| `2` | The inputs or segment media are missing, invalid, unsupported, or unverifiable (`segment-media-missing`, `segment-media-unexpected`, `segment-manifest-mismatch`, `segment-media-digest-mismatch`, `segment-manifest-invalid`, `unsupported-version`) |

On failure the SDK removes everything it placed in the result mount and writes one line
`socalytics-analyst: failed <category>` to standard error. Messages never carry payloads, file contents, or job fields
beyond contract identifiers. The Manager does not interpret the statuses; they serve the operator's bounded log.

Every SDK error derives from `AnalystSdkError` and has a `category`:

- Exit `2` (`InputError`): `input-missing`, `input-invalid`, `unsupported-contract-version`,
  `job-context-incomplete`, `input-staging-mismatch`, `input-artifact-mismatch`.
- Exit `1` (`ResultError`): `fact-out-of-window`, `fact-invalid`, `fact-payload-invalid`, `fact-kind-undeclared`,
  `artifact-invalid`, `validity-window-missing`,
  `validity-window-invalid`, `confidence-missing`, `result-value-invalid`, `result-unexpected-file`,
  `result-already-finalized`, `output-invalid`.
- Exit `1` for any other exception of the Analyst's code: `analyst-failed`.

## Supported contract versions

Versioning follows the contracts' major versions (all `v1`). The SDK accepts these minor versions as input and
validates its own output against the newest:

| Contract | Supported versions |
| --- | --- |
| `analysis/analyst-job` | 1.0.0, 1.1.0 |
| `analysis/analyst-attempt` | 1.0.0 |
| `analysis/upstream-results` | 1.0.0, 1.1.0 |
| `analysis/result-manifest` | 1.0.0, 1.1.0 (written as 1.1.0) |
| `analysis/fact-artifact` | 1.0.0 (written as 1.0.0) |

A document with another major or an unknown minor version fails with `unsupported-contract-version`.

## Supported commands

Run from `src/analysts/sdk/`. The environment's locked sequence (`UV_PYTHON_DOWNLOADS=never`, so `uv` uses the
environment's CPython 3.12) and the tests:

```text
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

On Windows use `.venv\Scripts\python.exe` for the build. The wheel contains `socalytics_analyst/contracts/`.

## Schema copy refresh rule

The package carries a copy of the canonical schemas it implements (`src/socalytics_analyst/contracts/`) so that it
works without the repository. The canonical files in the repository-root `contracts/` folder are authoritative. After
any change to an implemented analysis or common contract, refresh the copy in the same change and rerun the tests:

```text
python scripts/sync_contracts.py
```

`python scripts/sync_contracts.py --check` (or `uv run --no-sync python scripts/sync_contracts.py --check`) only
reports differences and exits non-zero when the copy is stale. The SDK tests also fail when a model field differs from
its schema or when a canonical valid or invalid example is not handled as its expectation states.

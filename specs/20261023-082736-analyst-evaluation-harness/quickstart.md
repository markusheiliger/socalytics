# Quickstart: Analyst Evaluation Harness

Validation guide for the finished feature. Run from the repository root unless stated; the pinned `ffmpeg` and
`ffprobe` must be on `PATH`. Contracts: [contracts/](contracts/); entities: [data-model.md](data-model.md).

## 1. Project tests

```bash
cd src/analysts/capabilities/analyst-evaluation-harness
export UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

Expected: all tests pass, including the end-to-end test that evaluates `reference-media-probe`.

## 2. Generate and validate the corpus (SC-006)

```bash
uv run --no-sync python scripts/generate_corpus.py --output /tmp/corpus-a
uv run --no-sync python scripts/generate_corpus.py --output /tmp/corpus-b
uv run --no-sync python -m analyst_evaluation_harness validate-corpus --corpus /tmp/corpus-a
diff /tmp/corpus-a/corpus-manifest.json /tmp/corpus-b/corpus-manifest.json
```

Expected: validation succeeds and the manifests (identity and clip digests) are identical. Changing one byte of a clip
makes `validate-corpus` exit 2 naming that clip.

## 3. Evaluate the reference capability (SC-001, SC-002)

Write an evaluation configuration (see [contracts/evaluation-config.schema.json](contracts/evaluation-config.schema.json))
with the label kind `frame-second-probe`, `primaryMetric` `f1`, `matchToleranceMs` `0`, and the comparator
`predict-nothing`, then:

```bash
uv run --no-sync python -m analyst_evaluation_harness evaluate --corpus /tmp/corpus-a --config /tmp/eval.json \
  --output /tmp/eval-1 --subject "<local command that runs reference_media_probe through scripts/local_entry.py with {mount}>" \
  --capability-id reference-media-probe --capability-version 1.0.0 --output-type foundational-facts
uv run --no-sync python -m analyst_evaluation_harness verify-report /tmp/eval-1/report.json
```

Expected: exit 0; per-profile metrics for `fixed` and `broadcast`; comparison verdict `beat` per profile (under that configuration); determinism
`passed`; lineage `not-evidenced` (subprocess mode records `not evaluated as an image`). Repeat into `/tmp/eval-2`:
both reports have the same `identity`. Changing one byte of `report.json` makes `verify-report` fail.

## 4. Container mode (manual evidence)

Build the reference image with the command of its README, resolve its digest, and pass `<repository>@sha256:<digest>`
as `--subject` with a local Docker-compatible engine. Expected: the report records mode `container` and the image
digest, lineage `passed`; with the image absent from the engine the harness exits 2 and pulls nothing. Docker is not
part of the automated verification.

## 5. Documentation

`node .github/scripts/check-markdown.mjs` passes after the documentation task.

The implementation records the outcome of steps 1 to 3 and 5 as a `## Validation record` section appended at the end of this file (task T054).

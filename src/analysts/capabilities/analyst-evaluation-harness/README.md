# Analyst Evaluation Harness

Development evaluation tooling (distribution `socalytics-analyst-evaluation-harness` 0.1.0, package
`analyst_evaluation_harness`) that evaluates one Analyst capability against a labelled corpus and a declared
comparator, and writes a reproducible, self-verifying report. It is the Wave 0 metrics harness of the
[Analyst Capability Catalog](../../../../docs/architecture/analyst-capability-catalog.md).

It is not a catalog capability: it is not an Analyst, has no manifest, image, or registry entry, and is never
published. It lives under `capabilities/` only because the environment tooling recognizes Python projects there. It
evidences no promotion: it applies no production threshold, makes no promotion decision, and its report contains no
promotion field.

## Setup and tests

Python 3.12, `uv`, and the pinned `ffmpeg` and `ffprobe` on `PATH` are required. From this directory:

```bash
export UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

The tests include an end-to-end evaluation of the [reference capability](../reference-media-probe/README.md) in
subprocess mode. They need no Docker.

## Commands

Run from this directory as `uv run --no-sync python -m analyst_evaluation_harness <command>`:

| Command | Purpose |
| --- | --- |
| `evaluate --corpus <dir> --config <file> --output <dir> --subject <ref> --capability-id <id> --capability-version <semver> --output-type <type>` | Validate the inputs, run the subject and the comparator on every clip, and write `<output>/report.json` and the per-run evidence under `<output>/runs/` |
| `validate-corpus --corpus <dir>` | Validate the manifest and every clip digest and print every problem |
| `verify-report <report.json>` | Recompute the report identity and validate the report schema |

`evaluate` prints the report path, its identity, and one line per gate (`<gate>: passed|failed|not-evidenced`). The
harness writes nothing outside `--output`.

| Exit code | Meaning |
| --- | --- |
| 0 | The evaluation ran on every clip without a failed clip (gate statuses, including failed or not-evidenced gates and a failed determinism repetition, are read from the report); also `validate-corpus` and `verify-report` succeeded |
| 1 | Unexpected error |
| 2 | Refused manifest, configuration, subject, or command line (every problem listed on standard error; no clip was run) |
| 3 | The evaluation ran and wrote the report, but at least one clip failed for the subject or the comparator |

## Corpus

A corpus is a directory with `corpus-manifest.json` and the clip files it names. The manifest follows
[schemas/corpus-manifest.schema.json](schemas/corpus-manifest.schema.json): `corpusId`, `corpusVersion`, an `identity`
(`sha-256:` of the canonical manifest without `identity`), required `rights` (`origin`, `license`, `production`), and
clips. Each clip has `clipId`, a relative `file`, a `sha-256:` content `digest`, a `profile` (`fixed` or `broadcast`),
`frameRate`, `durationMs`, `matchStartMs`, its own `rights`, and `labels` (an empty list measures false positives). A
label has a `labelKind` (the fact kind), either `positionMs` or `rangeStartMs` plus `rangeEndMs`, and an optional
`payload`.

Validation runs before any execution and reports every problem at once: schema violations, missing rights or
provenance, unknown profile, duplicate `clipId`, missing or unreadable files, digest or identity mismatch.

### Generating the synthetic corpus

```bash
uv run --no-sync python scripts/generate_corpus.py --output <empty dir>
```

The script writes four clips per profile with the pinned FFmpeg `testsrc2` source and bit-exact encoder flags, plus a
manifest whose `frame-second-probe` labels are known by construction (one clip has no labels). Two runs with the same
toolchain produce identical manifests. The corpus is generated and never committed.

## Configuration

The evaluation configuration follows [schemas/evaluation-config.schema.json](schemas/evaluation-config.schema.json):

| Field | Meaning |
| --- | --- |
| `configVersion` | `"1.0.0"` |
| `repetitions` | Executions per clip including the first (1 to 10, default 3) |
| `timeLimitSeconds` | Limit per execution (1 to 3600, default 120) |
| `labelKinds` | Per label kind: required `primaryMetric` (`precision`, `recall`, or `f1`) and `matchToleranceMs` (default 0) |
| `determinismTolerance` | Optional per kind `positionMs` and `numeric`; absent means byte-identical |
| `comparator` | Optional: `{"kind": "builtin", "rule": "predict-nothing" or "predict-everything"}`, or `{"kind": "capability", "reference", "version", "outputType"}`; absent means `not-declared` |
| `thresholds` | Optional list of `{labelKind, profile, metric, minimum}`; verdicts are labelled `evaluation-local` |

A label kind in the corpus without a `primaryMetric`, an unknown comparator rule, a negative tolerance, or a capability
reference that is neither an image digest reference nor a runnable command refuses the configuration (exit 2).

## Subject and comparator references

| Mode | Reference | Recorded |
| --- | --- | --- |
| `container` | `<repository>@sha256:<64 hex>`; the image must already be in the local engine (checked with `docker image inspect`) | `imageDigest` is the reference digest |
| `subprocess` | A local command line that may contain `{mount}`; the first token must be an existing executable | `imageDigest` is `null`, `imageDigestStatus` is `not evaluated as an image` |

The harness never pulls an image. The comparator comes only from the configuration. A built-in comparator runs in
process: `predict-nothing` writes no facts, and `predict-everything` writes one fact per covered match-second for every
configured label kind with an empty payload.

Every execution gets a fresh mount root with the staged job (`job.json`, contract 1.2.0 with `segmentMedia`),
`attempt.json`, the clip as segment media, and empty `result/` and `scratch/`. The result is validated like the
platform would: the facts and the result manifest are checked, and a failure is categorized by the SDK category,
`timeout`, `invalid-result`, or `execution-failed`.

`scripts/local_entry.py` is a standalone launcher for subprocess mode, because the SDK `run` reads `/socalytics` by
default. It imports `module:function` inside the capability's own environment, so the harness never imports the
capability. For the reference capability, from a locked environment of this project:

```bash
uv run --no-sync python -m analyst_evaluation_harness evaluate --corpus /tmp/corpus --config /tmp/eval.json \
  --output /tmp/eval-1 --capability-id reference-media-probe --capability-version 1.0.0 \
  --output-type foundational-facts \
  --subject "$PWD/.venv/bin/python $PWD/scripts/local_entry.py reference_media_probe.__main__:analyse --output-type foundational-facts --root {mount}"
```

## Matching and metrics

Label kinds are registered with a matching rule in `matching.py`; the first rule, `fact-position`, matches a fact of the
same kind whose `startMs` equals the label position, or lies inside the label range, within `matchToleranceMs`, and
compares the labelled payload keys by deep equality. Pairing is one-to-one by increasing distance with ties broken by
label order, then fact order. A pair that fails its payload expectation is a miss plus a false positive. Unpaired facts
of a labelled kind are false positives, unpaired labels are misses (a failed clip contributes its labels as misses),
and facts of a kind without labels are `unlabelled` and excluded. A new label kind adds a registry entry without
changing the report schema.

Counts are summed per profile and label kind (micro averages) before `precision = TP/(TP+FP)`, `recall = TP/(TP+FN)`,
and `f1 = 2TP/(2TP+FP+FN)` are formed; a ratio with a zero denominator is `null` with a `*Undefined` reason. There is
never an aggregate across profiles, and every metric traces to the clip pairings in the report.

## Gates and verdicts

Per profile and label kind the verdict is `beat` (both primary metrics defined and the capability's strictly higher),
`did-not-beat`, or `not-evidenced` (a metric is undefined or a run is incomplete). The profile verdict is
`not-evidenced` when any kind is, any clip of the profile failed, or no comparator is declared; otherwise
`did-not-beat` if any kind is, else `beat`.

Gates are `comparison.<profile>`, `determinism`, `lineage`, and `profileCoverage`, each `passed`, `failed`, or
`not-evidenced`. Determinism needs at least two executions per clip, lineage needs an image digest, and profile
coverage needs both profiles. A gate becomes `passed` or `failed` only from recorded results. Thresholds produce a
separate `thresholdVerdicts` list labelled `evaluation-local`.

## Determinism and lineage

Repetitions 2 to `repetitions` of each clip are compared with the first on the canonical form of the facts:
byte-identical, or within `determinismTolerance`. A failed repetition fails the check and names the clip and the
failure category, and the clip stays evaluated.

The report records the corpus identity, the subject and comparator references and image digests, the SDK and harness
versions, the `ffmpeg` and `ffprobe` version lines, and the Python version.

## Report

`report.json` follows [schemas/evaluation-report.schema.json](schemas/evaluation-report.schema.json) in canonical JSON
(sorted keys, UTF-8, ratios rounded to six decimals). Its `identity` is the `sha-256:` over the report without
`identity` and the non-identity `run` section (start and finish time, duration, host), so two evaluations of the same
inputs have the same identity. `verify-report` recomputes it, so changing one byte of the file fails the check. Its
sections are `corpus`, `subject`, `comparator`, `configuration`, `lineage`, `clips`, `profiles`, `determinism`,
`gates`, optional `thresholdVerdicts`, and `run`.

## Container mode and its manual evidence

Container mode runs the digest-pinned image with `--pull never`, `--network none`, `--read-only`, `--cap-drop ALL`,
`--security-opt no-new-privileges`, the invoking user, a process and memory limit (256 processes, 512 MiB), the job,
attempt, and media bind-mounted read-only under `/socalytics`, and `result/` and `scratch/` read-write. An overrun
stops the named container with `docker kill`. With the image absent from the engine the harness exits 2 and pulls
nothing.

Container mode is manual evidence, not part of the automated verification: no test needs Docker. Build the reference
image with the command of its [README](../reference-media-probe/README.md), resolve its digest, and pass
`<repository>@sha256:<digest>` as `--subject` with a local Docker-compatible engine; the report then records mode
`container` and the digest, and the `lineage` gate is `passed`.

## Limits

- No production thresholds: the thresholds are evaluation-local, and no catalog quality gate is applied or evidenced.
- No promotion decision: the report never says that a capability may be promoted.
- No pull: images must already be in the local engine, and nothing is fetched or published.
- No real footage: the only corpus is the generated synthetic one, with no third-party rights.
- No platform, Manager, or registry interaction, and no catalog capability is evaluated.

## Validation record

Steps run from this directory with the pinned `ffmpeg` and `ffprobe` on `PATH` (quickstart steps 1 to 3, then the
repository Markdown check):

1. `uv sync --locked --all-groups --no-install-local --no-build`, `uv sync --locked --all-groups --no-build-isolation`,
   `uv build --wheel --no-build-isolation --python .venv/bin/python`, and `uv run --no-sync pytest`: 311 tests passed,
   including the end-to-end evaluation of `reference-media-probe`.
2. `scripts/generate_corpus.py` run twice into different folders: `validate-corpus` succeeded and
   `corpus-manifest.json` was identical (`diff` empty).
3. `evaluate` of `reference-media-probe` with the comparator `predict-nothing`, run twice, each followed by
   `verify-report` (exit 0): both reports share the `identity`
   `sha-256:098f3a124fbfd1eeda8c678a0cf5a3dce9f03835efd8b0e5eb4a0f638e7c2fe2`; the gates `comparison.fixed`,
   `comparison.broadcast`, `determinism`, and `profileCoverage` passed and `lineage` is `not-evidenced`.
4. `node .github/scripts/check-markdown.mjs` from the repository root: 0 issues.

The one-byte tamper checks are covered by `test_corpus_validation.py` (changed clip) and `test_report_identity.py`
(changed report byte, with `test_cli.py` for the exit codes).

# Command Line Contract: Analyst Evaluation Harness

Run from `src/analysts/capabilities/analyst-evaluation-harness` as `uv run --no-sync python -m analyst_evaluation_harness <command>`.

## Commands

| Command | Purpose |
| --- | --- |
| `evaluate --corpus <dir> --config <file> --output <dir> --subject <ref> --capability-id <id> --capability-version <semver> --output-type <type>` | Validate, run the subject and the comparator on every clip, write `<output>/report.json` and the per-run evidence under `<output>/runs/` |
| `validate-corpus --corpus <dir>` | Validate the manifest and every clip digest; print every problem |
| `verify-report <report.json>` | Recompute the report identity and validate the report schema |

`--subject` is either `<repository>@sha256:<64 hex>` (container mode; the image must already be in the local engine)
or a local command line that may contain `{mount}` (subprocess mode). The harness never pulls an image. The
comparator comes only from the evaluation configuration.

`scripts/generate_corpus.py --output <empty dir>` writes the synthetic corpus and its `corpus-manifest.json`.

## Exit results

| Code | Meaning |
| --- | --- |
| 0 | The evaluation ran on every clip without a failed clip (the gate statuses, including failed or not evidenced ones and a failed determinism repetition, are read from the report); also `validate-corpus` and `verify-report` succeeded |
| 1 | Unexpected error |
| 2 | Refused manifest or configuration (every problem listed on standard error; no clip was run) |
| 3 | The evaluation ran and wrote the report, but at least one clip failed for the subject or the comparator |

## Output

Standard output prints the report path, its identity, and one line per gate (`<gate>: passed|failed|not-evidenced`).
The report follows [evaluation-report.schema.json](evaluation-report.schema.json); the inputs follow
[corpus-manifest.schema.json](corpus-manifest.schema.json) and
[evaluation-config.schema.json](evaluation-config.schema.json). The harness writes nothing outside `--output`.

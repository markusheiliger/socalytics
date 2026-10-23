# Implementation Plan: Analyst Evaluation Harness

**Branch**: `20261023-082736-analyst-evaluation-harness` | **Date**: 2026-10-23 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/20261023-082736-analyst-evaluation-harness/spec.md`

## Summary

A new Python 3.12 `uv` project, `analyst-evaluation-harness` (distribution `socalytics-analyst-evaluation-harness`,
package `analyst_evaluation_harness`), evaluates one Analyst capability against a labelled corpus and a declared
comparator and writes a versioned, content-addressed report. It stages every clip the way a Manager stages segment
media (`job.json`, `attempt.json`, `media/segment-manifest.json`, `media/segment.mp4`, empty `result/` and
`scratch/`), runs the capability by the SDK's container interface either as a container run from an image digest
reference (local engine only, never a pull) or as a subprocess run of a local command, reads the written
`result/facts.json`, and pairs facts with labels one-to-one under a per-label-kind tolerance. It computes precision,
recall, and F1 per label kind and source profile, evaluates a built-in trivial comparator (`predict-nothing`,
`predict-everything`) or a capability reference through the same path, repeats runs for the determinism check, and
records lineage. A generation script in the same project writes the synthetic rights-free corpus with the pinned
FFmpeg. The first evidence evaluates `reference-media-probe` end to end. The report, corpus-manifest, and
evaluation-configuration schemas are component-local contracts owned by the harness project; no platform contract,
API, Web UI, registry, or Manager code changes.

## Technical Context

**Language/Version**: Python 3.12 (`requires-python = ">=3.12,<3.13"`), the same convention as the SDK and the
reference capability.

**Primary Dependencies**: the Analyst SDK `socalytics-analyst-sdk` (path dependency `../../sdk`; contract models for
the job, attempt, fact artifact, and segment manifest, and the error categories) and `jsonschema` (already used by
the repository's Python projects) to validate the corpus manifest, configuration, and report schemas. Standard
library otherwise (`subprocess`, `hashlib`, `json`, `decimal`). Dev group: `hatchling`, `pytest`, and the reference
capability as a path dependency (`../reference-media-probe`) for the end-to-end test. No new framework, SDK, or
model.

**Storage**: Files only: the corpus directory (clips and `corpus-manifest.json`) and a per-evaluation output
directory (`report.json` and per-run evidence under `runs/`). Nothing is written outside the output directory.

**Testing**: `pytest`. Unit tests for validation, matching, metrics, gates, canonical identity, and the determinism
check; subprocess-mode tests with test-only capability fixtures (a scripted Analyst built on the SDK's `run`); a
container-mode test against a fake `docker` executable on `PATH` that asserts the exact argument list (no pull,
restricted run, named container, kill on overrun); and one end-to-end test that generates the corpus with the pinned
FFmpeg and evaluates `reference-media-probe` in subprocess mode with both built-in comparators. A real
container-engine run of the reference image is documented as manual evidence in [quickstart.md](quickstart.md); it is
not part of the automated verification.

**Target Platform**: Linux x86_64 development and verification environment with the pinned `ffmpeg` and `ffprobe` on
`PATH` (provided by `.github/actions/environment-setup`); container mode additionally needs a local
Docker-compatible engine when used.

**Project Type**: Python command-line tool (library plus `python -m analyst_evaluation_harness`) under
`src/analysts/`.

**Performance Goals**: SC-001: one command evaluates `reference-media-probe` on the generated corpus (four clips of
5 to 6 seconds per profile, three executions per clip) in under 10 minutes; expected to be well under one minute in
subprocess mode.

**Constraints**: Offline (no network, no pull, no model weights, no real footage); deterministic report (canonical
JSON, sorted keys, metrics rounded to six decimals, run time outside the identity); per-execution time limit; a clip
failure never stops the evaluation; never writes outside the output directory; the exit result reports only whether
the evaluation ran.

**Scale/Scope**: A handful of short synthetic clips per profile; one subject and one comparator per evaluation; no
aggregate score; no production thresholds and no promotion decision.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Principle or constraint | Assessment |
| --- | --- |
| I. Architecture is the design authority | Pass. The harness implements the Wave 0 "metrics harness" and the comparator, determinism, and lineage gates of `analyst-capability-catalog.md`. The `## Architecture Updates` section below names the narratives to correct; the documentation task makes them describe the implemented state. No design diverges from the architecture. |
| II. Respect source-area ownership | Pass. The project lives under `src/analysts/`, in the one place the Analyst environment verification recognizes for a Python project besides the SDK: `src/analysts/capabilities/<name>`. No new immediate child of `src/`. It is evaluation tooling and not a catalog capability; the READMEs say so (research R1). |
| III. API-first control plane | Pass, not applicable to the platform: the harness has no platform, Manager, or registry interaction. Its report schemas are component-local executable contracts that live with the component that validates them. |
| IV. Evidence over claims | Pass. pytest evidence (unit, subprocess, fake-engine container, and end-to-end) runs in the project's own suite; nothing claims production readiness, a promotion decision, or deployment support. |
| V. Focused, minimal changes | Pass. One new project plus documentation; the SDK, the reference capability, `contracts/`, the platform, the Manager, and the Web UI are unchanged. |
| Technology and tooling | Pass. Python 3.12 and `uv`, the pinned FFmpeg and ffprobe, and `jsonschema`, all already present in the repository's Python projects. Environment check: `.github/actions/environment-setup` installs CPython 3.12, `uv`, FFmpeg, and ffprobe, and `.github/actions/environment-verify` discovers `src/analysts/capabilities/<name>` projects, so every task is verifiable without an environment feature. Docker is not required by the automated tests. |

No violations; Complexity Tracking stays empty. Post-design re-check: unchanged (the design added no project, no
runtime dependency beyond `jsonschema`, no platform contract, and no tooling).

## Project Structure

### Documentation (this feature)

```text
specs/20261023-082736-analyst-evaluation-harness/
├── plan.md              # This file
├── research.md          # Phase 0 decisions
├── data-model.md        # Phase 1 entities, rules, and states
├── quickstart.md        # Phase 1 validation guide
├── contracts/           # Phase 1: schemas and the command line contract
│   ├── corpus-manifest.schema.json
│   ├── evaluation-config.schema.json
│   ├── evaluation-report.schema.json
│   └── cli.md
└── tasks.md             # Phase 2 output (/speckit-tasks - NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/analysts/capabilities/analyst-evaluation-harness/
├── README.md                                  # author documentation: commands, formats, gates, limits
├── pyproject.toml                             # socalytics-analyst-evaluation-harness, uv sources: SDK
├── uv.lock
├── schemas/
│   ├── corpus-manifest.schema.json            # copied from this plan's contracts
│   ├── evaluation-config.schema.json
│   └── evaluation-report.schema.json
├── scripts/
│   ├── generate_corpus.py                     # deterministic synthetic corpus (pinned ffmpeg)
│   └── local_entry.py                         # launcher: runs `module:function` through socalytics_analyst.run on a mount root
├── src/analyst_evaluation_harness/
│   ├── __init__.py                            # version
│   ├── __main__.py                            # command line: evaluate, validate-corpus, verify-report
│   ├── canonical.py                           # canonical JSON, sha-256 identities
│   ├── corpus.py                              # manifest model, schema and digest validation
│   ├── config.py                              # evaluation configuration model and refusals
│   ├── staging.py                             # Manager-style mount root per clip
│   ├── runners.py                             # container, subprocess, and built-in trivial runners
│   ├── facts.py                               # reads and validates result/facts.json
│   ├── matching.py                            # label kinds, matching rules, one-to-one pairing
│   ├── metrics.py                             # precision, recall, F1, undefined handling
│   ├── determinism.py                         # repetition comparison with tolerances
│   ├── gates.py                               # verdicts and gate statuses
│   ├── report.py                              # report assembly, identity, verification
│   └── evaluate.py                            # orchestration
└── tests/                                     # unit, subprocess, fake-engine, and end-to-end tests
```

**Structure Decision**: One new project at `src/analysts/capabilities/analyst-evaluation-harness/`. The Analyst
project discovery of the environment verification recognizes Python manifests only at `src/analysts/sdk` and
`src/analysts/capabilities/<name>` and fails every other manifest as an unsupported layout, so this location needs no
environment feature and gets locked, tested, and covered like the reference capability. The tooling nature is stated
in the READMEs rather than by a new directory.

## Complexity Tracking

No Constitution Check violations.

## Architecture Updates

### Narratives

- [Analyst Capability Catalog](../../docs/architecture/analyst-capability-catalog.md), sections "Implementation
  Waves" (Wave 0 "metrics harness") and "Validation And Promotion Gates" (comparator, determinism, and lineage
  gates): the harness is the executable evidence tool for these gates.
- [Analysts, Models, and Hardware](../../docs/architecture/analysts-models-and-hardware.md), "Image Metadata And
  Packaging": the harness runs the reference Analyst's container interface and records the OCI digest lineage.
- [Platform Implementation](../../docs/architecture/platform-implementation.md), the source-area ownership list for
  `src/analysts`: gains the harness project.

### Design updates

- Applied now to `analyst-capability-catalog.md` (new paragraph after the development reference capability
  paragraph), worded as decided but not implemented: the Wave 0 metrics harness is development tooling in
  `src/analysts/capabilities/analyst-evaluation-harness`, with its execution modes, matching, per-profile metrics,
  comparator, determinism, lineage, and not-evidenced rules. The implementation turns it into present tense.

### Status statements at merge

- `analyst-capability-catalog.md`, "Implementation Waves": Wave 0 "metrics harness" must state that the harness
  exists as development tooling while the corpus, annotations, and model cards remain future.
- `analyst-capability-catalog.md`, new harness paragraph: "decided but not implemented yet" becomes present tense.
- `analyst-capability-catalog.md`, "Development reference capabilities are not part of this catalog": the
  harness is likewise not a catalog capability and evidences no promotion.
- `platform-implementation.md`, source ownership list: "the catalog's Analyst capability implementations ... remain
  future" must also name the harness project as evaluation tooling under `src/analysts/capabilities`.
- `analysts-models-and-hardware.md`, "Image Metadata And Packaging" and the Docker Desktop adapter evidence
  paragraph: add the harness as a consumer of the reference image only if the implemented text says so, and keep the
  statement that the capability passes no gate.

The documentation task must make these narratives and every status statement the feature affects true for the
merged state, re-reading `docs/architecture/` at implementation time (including the repository `README.md`,
`src/analysts/README.md`, and `AGENTS.md` wording about what exists under `capabilities/`); the listed rewrites are
examples, not the whole job.

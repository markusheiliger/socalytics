# Research: Analyst Evaluation Harness

No `NEEDS CLARIFICATION` remains; the spec's clarifications fix the behavior, and this file records the technical decisions that the plan relies on.

## R1 - Project location and name

- **Decision**: `src/analysts/capabilities/analyst-evaluation-harness/` (distribution
  `socalytics-analyst-evaluation-harness`, package `analyst_evaluation_harness`).
- **Rationale**: `.github/actions/environment-verify/analyst-projects.py` recognizes Python projects only at
  `src/analysts/sdk` and `src/analysts/capabilities/<name>` and reports any other manifest under `src/analysts` as an
  unsupported layout. Placing the harness there keeps it locked, tested, and covered without an environment feature
  and without a new first-level or second-level area. The READMEs state that it is evaluation tooling, not a catalog
  capability.
- **Alternatives**: `src/analysts/evaluation/` (rejected: unsupported layout, needs an environment spec first);
  `src/analysts/sdk/` extra (rejected: couples tooling to the published-contract SDK).

## R2 - Executing a capability

- **Decision**: A common staging step writes one mount root per execution (`job.json` validated by the SDK
  `LogicalJob` model at contract 1.2.0 with `segmentMedia`, `attempt.json`, `media/segment-manifest.json`,
  `media/segment.mp4`, empty `result/` and `scratch/`), with fresh directories per run. Runners:
  - **container**: `docker run` of `<name>@sha256:<digest>` with `--pull never` semantic (the harness first runs
    `docker image inspect <ref>` and refuses the configuration when absent), `--network none`, `--read-only`,
    `--cap-drop ALL`, `--security-opt no-new-privileges`, the invoking user, a pids and memory limit, the job, attempt,
    and media bind-mounted read-only at `/socalytics/...`, `result/` and `scratch/` read-write, and a unique container
    name so an overrun can be stopped with `docker kill`.
  - **subprocess**: the local command is split with `shlex`, the placeholder `{mount}` is replaced by the mount root,
    the working directory is the run folder, and the environment is minimal (`PATH` only). The SDK's `run` reads
    `/socalytics` by default; the harness ships `scripts/local_entry.py`, a standalone launcher that imports
    `module:function` inside the capability's own environment and calls `socalytics_analyst.run(analyst,
    output_type=..., root=<mount>)`, so the harness never imports the capability.
  - **built-in** (comparators only): produces the fact artifact in process, without any execution.
- **Rationale**: This follows the SDK's container interface exactly (README "Container interface"), mirrors the
  Manager's restrictions closely enough to catch interface violations, and needs no SDK change.
- **Alternatives**: Add an environment variable for the root to the SDK (rejected: changes a shipped SDK for tooling
  convenience and the reference capability forbids environment reads); pull images (rejected by the spec).

## R3 - Corpus format, digests, and identity

- **Decision**: `corpus-manifest.json` (schema in [contracts/corpus-manifest.schema.json](contracts/corpus-manifest.schema.json))
  lists clips with `clipId`, `file` (relative, no traversal), `digest` (`sha-256:<64 hex>`), `profile`, `frameRate`,
  `durationMs`, `matchStartMs`, rights and provenance, and `labels`. Its `identity` is the `sha-256` of the canonical
  JSON of the manifest without the `identity` field. Validation runs before any execution and lists every problem.
- **Rationale**: Content-derived identity without a separate hash tool; the same canonical form is reused for the report.
- **Alternatives**: Directory hash (rejected: sensitive to unrelated files).

## R4 - Canonical JSON and report identity

- **Decision**: Canonical form = UTF-8 JSON with sorted keys, separators `(",", ":")`, `ensure_ascii=False`, no NaN;
  metric ratios are rounded to six decimals with `Decimal` ROUND_HALF_EVEN before serialization. The report `identity`
  is the `sha-256` of the canonical form of the report without `identity` and `run` (the non-identity fields: start
  and finish time, duration, harness host). The file is written as that canonical form plus a trailing newline;
  `verify-report` recomputes the identity.
- **Rationale**: CPython's `repr`-based float output is stable across machines for rounded values; excluding `run` keeps
  two evaluations byte-identical outside it.
- **Alternatives**: RFC 8785 library (rejected: new dependency).

## R5 - Matching and metrics

- **Decision**: Label kinds are registered in a table `{kind: matching rule}`; the first rule is
  `fact-position`: same `kind`, and the fact's `startMs` equals the label's `positionMs` within `tolerance` (default
  0), or lies in `[rangeStartMs - tolerance, rangeEndMs + tolerance]` for a range label; the payload is compared
  (deep equality of the labelled keys only) when the label carries `payload`. Pairing is one-to-one by increasing
  distance, ties broken by label order and then fact order (stable sort, greedy). Unpaired facts of a labelled kind
  are false positives; unpaired labels are misses; facts of a kind with no label in the corpus are `unlabelled` and
  excluded from the metrics. A new kind adds a table entry without changing the report's per-profile structure
  (FR-018).
- **Metrics**: `precision = TP/(TP+FP)` (undefined when the denominator is 0), `recall = TP/(TP+FN)` (undefined
  when 0), `f1 = 2TP/(2TP+FP+FN)` (undefined when the denominator is 0). An undefined value is `null` with a
  `*Undefined` reason. A failed clip contributes its labels as FN and no facts. Counts are summed per profile and
  label kind before ratios are formed (micro averages); there is never an aggregate across profiles.
- **Rationale**: F1 from counts stays defined for a comparator that predicts nothing while labels exist (0), as the
  clarifications require precision to be undefined there; a primary metric of `f1` (or `recall`) therefore stays
  usable.
- **Alternatives**: Macro averages over clips (rejected: unstable on a handful of clips).

## R6 - Verdicts and gates

- **Decision**: Per profile and label kind: `beat` when both primary metrics are defined and the capability's is
  strictly higher; `did-not-beat` when both are defined and it is not; `not-evidenced` when either is undefined or the
  comparator or the capability is incomplete. Profile verdict: `not-evidenced` if any kind is `not-evidenced` or any
  clip of the profile failed (capability or comparator) or no comparator is declared; otherwise `did-not-beat` if any
  kind is `did-not-beat`; else `beat`. Gate statuses are `passed`, `failed`, `not-evidenced`: comparison per profile
  (`passed` for `beat`, `failed` for `did-not-beat`), determinism (needs at least two executions per clip),
  lineage (needs an image digest), and profile coverage (both profiles present). Evaluation-local thresholds, when
  supplied, produce a separate `thresholdVerdicts` list labelled `evaluation-local`; no promotion field exists.
- **Rationale**: Encodes FR-007, FR-008, FR-014, and FR-015 once, in `gates.py`, so no other module decides a status.

## R7 - Determinism check

- **Decision**: `repetitions` is the total number of executions per clip (default 3, at least 2 to evidence the
  gate; 1 means `not-evidenced`). The first run feeds the metrics; runs 2..n are compared with run 1 on the canonical
  form of the facts. Without a tolerance for a fact kind the canonical JSON must be byte-identical; a tolerance
  `{positionMs, numeric}` per label or fact kind allows `startMs` and `endMs` to differ by `positionMs` and
  `confidence` and every numeric payload leaf to differ by `numeric`, while fact counts, kinds, and non-numeric values
  must be equal. A failed repetition fails the check and names the clip and the SDK failure category; the clip stays
  evaluated.
- **Alternatives**: Compare the report-level metrics only (rejected: hides fact-level differences).

## R8 - Failure categories and exit results

- **Decision**: Category of a failed execution: the category the SDK prints on standard error (`socalytics-analyst:
  failed <category>`), `timeout` for an overrun, `invalid-result` when `facts.json` or the manifest does not validate,
  and `execution-failed` when no category line exists. Command-line exit codes: 0 evaluation ran on every clip, 1 an
  unexpected error, 2 refused manifest or configuration, 3 the evaluation ran with at least one failed clip (the
  report is still written). A failed repetition alone exits 0 (FR-019).
- **Rationale**: Reuses the SDK's categories without importing them as a requirement of the capability.

## R9 - Synthetic corpus generation

- **Decision**: `scripts/generate_corpus.py --output DIR` writes four clips per profile with `ffmpeg` `testsrc2`
  (`-c:v mpeg4 -threads 1 -fflags +bitexact -flags:v +bitexact -map_metadata -1 -an`, keyframe interval equal to
  the frame rate). `fixed` stands in with 25 fps and a 64x64 frame at match offset 0; `broadcast` with 30 fps, a
  128x72 frame, and a non-zero `matchStartMs`; durations 5 and 6 seconds. Labels are known by construction: one
  `frame-second-probe` label per match-second with `payload.decodedFrames` equal to the frame rate (the last partial
  second of a clip gets its computed count), plus one clip with a deliberately empty label list. Rights and
  provenance state `synthetic`, `no third-party rights`, and the script and ffmpeg version that produced them.
  The corpus is generated, never committed.
- **Rationale**: Mirrors the reference capability's own synthetic media helper; bit-exact flags make two runs on the
  same pinned toolchain identical (SC-006).
- **Alternatives**: Commit clips (rejected: binary files in Git, digests tied to one toolchain).

## R10 - Contract placement

- **Decision**: Corpus manifest, evaluation configuration, and report schemas (JSON Schema 2020-12) live in the
  project's `schemas/` folder, copied from this plan's `contracts/`, and are validated by the project's tests; they
  are not added to the repository-root `contracts/` folder (no platform, Manager, or SDK consumer exists).
- **Rationale**: Component-local executable contracts belong with the component that validates them.

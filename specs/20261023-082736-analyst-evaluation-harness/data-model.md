# Data Model: Analyst Evaluation Harness

All documents are UTF-8 JSON in the canonical form of [research.md](research.md#r4---canonical-json-and-report-identity)
when the harness writes them; schemas are in [contracts/](contracts/).

## Corpus manifest

| Field | Rule |
| --- | --- |
| `manifestVersion` | `"1.0.0"` |
| `corpusId`, `corpusVersion` | non-empty key and semantic version |
| `identity` | `sha-256:` of the canonical manifest without `identity`; verified |
| `rights` | `origin`, `license`, `production` (how the clips and labels were produced), all required and non-empty |
| `clips[]` | at least one; `clipId` unique |

**Clip**: `clipId`; `file` (relative path inside the corpus directory, no `..`); `digest` (`sha-256:<64 hex>`,
verified against the file); `profile` (`fixed` or `broadcast`); `frameRate`; `durationMs`; `matchStartMs` (match time
of media time 0); `rights` (same three fields as the corpus); `labels[]` (may be empty, which measures false
positives).

**Label**: `labelKind` (the fact kind); `positionMs` or `rangeStartMs` plus `rangeEndMs` (exactly one form);
optional `payload` (object whose keys must equal the fact payload's keys of the same name).

Validation (before any execution; every problem listed): schema violation, missing rights or provenance field,
unknown profile, duplicate `clipId`, digest not a content digest, file missing or unreadable, digest mismatch,
identity mismatch.

## Evaluation configuration

| Field | Rule |
| --- | --- |
| `configVersion` | `"1.0.0"` |
| `repetitions` | integer 1..10 executions per clip including the first (default 3) |
| `timeLimitSeconds` | per execution, 1..3600 (default 120) |
| `labelKinds` | object keyed by label kind: `primaryMetric` (`precision`, `recall`, or `f1`, required), `matchToleranceMs` (default 0) |
| `determinismTolerance` | optional object keyed by kind: `positionMs`, `numeric` (non-negative); absent means byte-identical |
| `comparator` | optional; `{kind: "builtin", rule: "predict-nothing" \| "predict-everything"}` or `{kind: "capability", reference, version, outputType, entry?}`; absent means `not declared` |
| `thresholds` | optional list of `{labelKind, profile, metric, minimum}`; verdicts are labelled evaluation-local |

A label kind present in the corpus without `primaryMetric` is a refused configuration, as is an unknown comparator
rule, a negative tolerance, or a capability reference that is neither an image digest reference nor a runnable local
command.

## Capability reference (subject and comparator)

| Mode | Reference | Recorded |
| --- | --- | --- |
| `container` | `<repository>@sha256:<64 hex>`; must exist in the local engine (checked with `image inspect`, never pulled) | `imageDigest` = the reference digest |
| `subprocess` | command line with `{mount}` placeholder, the first token an existing executable | `imageDigest` = null, `imageDigestStatus` = `not evaluated as an image` |

The subject also carries `capabilityId`, `capabilityVersion`, and the `outputType` (`foundational-facts` or
`soccer-concepts`) the staged attempt expects.

## Execution result (per clip, per run)

`status` (`succeeded`, `failed`), `failureCategory` (SDK category, `timeout`, `invalid-result`, or
`execution-failed`), `facts` (validated list; empty when failed), `durationMs` (non-identity, kept under `run`).
State per clip and subject: first run `succeeded` or `failed`; repetitions only feed determinism.

## Pairing result (per clip, per subject, per label kind)

`pairs[]` (`labelIndex`, `factIndex`, `distanceMs`, `payloadMatch` true only for pairs counted as true positives),
`missedLabels[]` (label indexes, with the reason `no-fact`, `payload-mismatch`, or `clip-failed`),
`falsePositives[]` (fact indexes), and `unlabelled[]` (fact indexes of kinds without labels). The distance of a pair is `0` when the fact lies
inside the unwidened label range, otherwise the distance to the nearest range end (the distance to the position for a
label without a range). A pair whose payload expectation is not met is a miss plus a false positive, not a true positive.

## Built-in comparator facts

`predict-nothing` writes no facts. `predict-everything` takes the covered seconds from the clip's segment manifest
(`matchStartMs` plus the duration) and writes one fact per second for every label kind declared in the evaluation
configuration's `labelKinds`, with an empty payload, so payload expectations count as `payload-mismatch`.

## Metrics (per profile, per label kind, per subject)

`truePositives`, `falsePositives`, `falseNegatives`, `precision`, `recall`, `f1` (number or null), and
`precisionUndefined`, `recallUndefined`, `f1Undefined` reasons when null. The subject is the capability or the
comparator. There is no aggregate across profiles.

## Verdicts and gates

- Label-kind verdict: `beat`, `did-not-beat`, `not-evidenced`, with the primary metric, both values, and the margin
  (capability minus comparator) when both are defined.
- Profile verdict: derived exactly as in [research.md](research.md#r6---verdicts-and-gates).
- Gate status: `passed`, `failed`, `not-evidenced`. Gates: `comparison.<profile>`, `determinism`, `lineage`,
  `profileCoverage`. A missing profile, no comparator, an incomplete or failed comparator or capability run for the
  profile, fewer than two executions per clip, or a missing image digest yields `not-evidenced`.

State transitions: a gate starts as `not-evidenced`; it becomes `passed` or `failed` only from recorded results.

## Evaluation report

| Section | Content |
| --- | --- |
| `reportVersion`, `identity` | `"1.0.0"`; `sha-256:` over the report without `identity` and `run` |
| `corpus` | `corpusId`, `corpusVersion`, `identity`, clip count per profile |
| `subject` | capability id and version, mode, reference, `imageDigest`, `imageDigestStatus`, `outputType` |
| `comparator` | `kind` (`builtin`, `capability`, `not-declared`), rule or reference and version, same lineage fields when a capability |
| `configuration` | the evaluation configuration as read |
| `lineage` | SDK version, harness version, `ffmpeg` and `ffprobe` version lines, Python version |
| `clips[]` | per clip: `clipId`, `profile`, subject and comparator execution status and category, pairing results per label kind |
| `profiles` | per profile (`fixed`, `broadcast`): `status` (`evidenced` or `not-evidenced`), metrics for subject and comparator per label kind, label-kind verdicts, profile verdict |
| `determinism` | `status`, `repetitions`, `differences[]` (`clipId`, `run`, `category` or `factIndex`, `field`) |
| `gates` | the gate statuses above |
| `thresholdVerdicts` | optional, each labelled `evaluation-local` |
| `run` | non-identity: `startedAt`, `finishedAt`, `durationSeconds`, `host` |

Every metric traces to the clip pairings; no execution is needed to recompute it (SC-007). Report fields never
contain a promotion decision.

# Data Model: Finalize Verification Without Repeated Work

The action keeps no database; its only state is one small file per job.

## Finalize result record

File: `$RUNNER_TEMP/socalytics-environment-verify/finalize-<run id>-<attempt>-<job>.json` (see
[research.md](research.md) R2). It is read and written only by a `finalize` run of the action and dies with the job's
runner temporary folder.

| Field | Type | Meaning |
| --- | --- | --- |
| `version` | number | Record format, currently `1`; any other value is ignored |
| `passed` | boolean | `true` only for a run that passed completely; a record with anything else is ignored |
| `identity` | string | Workspace state identity at the start of the run (equal to the one at its end) |
| `mode` | string | Always `finalize` |
| `changedFiles` | string | SHA-256 of the normalized changed-files content |
| `scope` | string | `full` or `environment` |
| `checks` | string | The run's `checks` output |
| `uncovered` | string | The run's `uncovered` output |
| `flaky` | string | The run's `flaky` output |
| `source` | object | `runId`, `attempt`, `job`, `recordedAt` (UTC ISO time), `sequence` (the finalize run's number within the job, `sequence` of the valid record being replaced plus 1, and 1 when there is no valid record) |

Validation: every field present with its type; `passed === true`; `version === 1`. A record that fails any rule is
treated as absent.

## Workspace state identity

`sha256(HEAD commit + "\n" + tree)`; the tree is a hash of every file that is committed or uncommitted and not
ignored, minus the caller's `log-file` and `changed-files`. It is *uncertain* (no value) when Git cannot answer or an
ignored path other than a known generated or dependency folder (including `src/clients/web/src/generated` and `.specify/workflows/runs`, matched as path suffixes) exists (research R3).

## Changed-files content

The set of paths of the `changed-files` input, with `\r` removed and blank lines dropped, sorted unique, hashed
(research R4).

## Scope

| Scope | When | Checks that run (in the existing order) |
| --- | --- | --- |
| `full` | any other finalize run, task and phase runs | as today |
| `environment` | finalize mode and every changed path lies in `.github/actions/environment-(setup\|verify)/` (research R5) | Environment action tests, Markdown check |

## State transitions of a finalize run

```text
start -> identity certain? -- no --> run scope in full (no record read or written), report "reuse not possible"
                          -- yes -> record matches and passed? -- yes --> reuse: report, exit 0
                                                              -- no  --> run scope in full
run scope in full -> passed and identity unchanged at the end -> write record (replace)
                  -> passed but identity changed at the end   -> no record (report it), delete existing record
                  -> failed                                   -> delete existing record, exit non-zero
```

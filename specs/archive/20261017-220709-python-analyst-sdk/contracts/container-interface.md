# Contract: Analyst Container Interface (completed)

Extends the Docker runtime adapter's
[analyst-container contract](../../20261015-092159-docker-runtime-adapter/contracts/analyst-container.md);
everything not listed here is unchanged (non-root user, read-only root filesystem, no network, no environment
variables, no credentials, labels, outcome rules, bounded log).

## Mounts

| Path in the container | Access | Content | Change |
| --- | --- | --- | --- |
| `/socalytics/job.json` | read-only | The Logical Job message exactly as received from the work queue (bytes unchanged), valid against `analysis/analyst-job` v1 | was a reduced re-serialization |
| `/socalytics/attempt.json` | read-only | Attempt context, valid against `analysis/analyst-attempt` v1 | new |
| `/socalytics/inputs/` | read-only | Staged upstream results ([staged-inputs](../../20261013-134112-analysis-result-read/contracts/staged-inputs.md)); `coverage.json` follows upstream-results 1.0.0 or 1.1.0 | unchanged |
| `/socalytics/result/` | read-write | `manifest.json` and the artifacts it declares; top-level files only are stored | unchanged |
| `/socalytics/scratch/` | read-write | Working space | unchanged |

Both files are written by the Manager into the attempt directory before launch and deleted with it. Both runtimes
(simulated and Docker Desktop) see the same files.

## Outcome rules

Unchanged: exit 0 with a valid `manifest.json` succeeds; anything else fails with `analyst-error` (or the existing
resource, image, runtime, and timeout categories). The SDK's distinct non-zero statuses (1, 2) are for the
operator's bounded log only; the Manager does not interpret them.

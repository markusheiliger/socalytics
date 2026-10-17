# Contract: Analyst Container Interface

What the Analyst Manager provides to, and expects from, every Analyst container
it launches on the `docker-desktop` profile (research R6–R8). Image authors
build against this interface; the Manager enforces it.

## Mounts

| Path in the container | Access | Content |
| --- | --- | --- |
| `/socalytics/job.json` | read-only | The Logical Job message, valid against `contracts/analysis/analyst-job/v1`. Contains no credentials, grants, or platform addresses. |
| `/socalytics/inputs/` | read-only | Staged upstream results, laid out as the `staged-inputs` contract of Analysis Result Read for Jobs specifies. Absent for jobs without upstream dependencies. |
| `/socalytics/result/` | read-write | The Analyst writes `manifest.json` (valid against `contracts/analysis/result-manifest/v1`) and the artifacts it names. Nothing else is read from here. |
| `/socalytics/scratch/` | read-write | Working space; discarded with the attempt. |
| `/tmp` | read-write | Memory-backed, 64 MiB. |

The root filesystem is read-only. No other host path is mounted.

## Environment and identity

- The container runs as a non-root user (`65532:65532`, or the Manager's own
  non-root uid:gid on Unix hosts); an image's `USER` is overridden.
- No environment variables are set by the Manager; no credentials, tokens,
  grant URLs, broker or platform addresses are provided in any form.
- Networking is disabled (`none`); Analysts read inputs only from the mounts.
- No added capabilities (`--cap-drop ALL`), `no-new-privileges`, no
  privileged mode, no devices, no host network, PID, or IPC namespace, and no
  container runtime socket.
- CPU and memory are limited to the job's pinned resources; swap equals the
  memory limit; at most 512 processes.

## Labels set by the Manager

`io.socalytics.manager`, `io.socalytics.logical-job`, `io.socalytics.attempt`
(see [data-model.md](../data-model.md#analyst-container-runtime-object)).

## Outcome rules

| Container end | Attempt outcome (attempt-completion category) |
| --- | --- |
| Exit 0 and a valid `manifest.json` | Success; the Manager stores and completes the result. |
| Exit 0 without a valid `manifest.json` | Failed: `analyst-error`. |
| Non-zero exit | Failed: `analyst-error`. |
| Killed for exceeding the memory limit | Failed: `resource-exhausted`. |
| Execution timeout reached | Stopped; failed: `execution-timeout`. |
| Image unavailable or outside the registry prefix | Failed before launch: `image-unavailable`. |
| Runtime lost during the attempt | Failed: `runtime-unavailable`, or recovery through lease expiry. |

Standard output and standard error are kept on the host as a bounded log
(1 MiB) for the operator and are never sent to the platform.

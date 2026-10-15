# Quickstart: Docker Desktop Runtime Adapter for Analyst Execution

Validation scenarios that prove the feature end to end. Commands run from the
repository root.

## Prerequisites

- .NET SDK pinned by `src/platform/global.json` and
  `src/analysts/manager/global.json`.
- A running Docker-compatible engine (already required by the platform and
  Integration tests).
- Linux only for the Manager's PKCS#11 tests and the end-to-end Docker test:
  SoftHSM2, as for the existing Manager tests.

## Automated verification

```powershell
dotnet restore src/platform/SocAlytics.Platform.slnx
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Integration.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
dotnet test src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests --no-build
dotnet restore src/analysts/manager/SocAlytics.Analysts.Manager.slnx
dotnet build src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-restore
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

| Suite | Proves |
| --- | --- |
| Manager tests (fake Engine API handler) | Endpoint policy (R2), probe mapping and compatibility matching (R3), launch settings and labels (R6, R7), outcome mapping (R8), reconciliation filters (R9), image cache rules (R10), offer filtering and reasons (R5); FR-002–FR-018, FR-022–FR-028. |
| Integration tests | `registry-credentials` (bearer from a token-auth registry fake, `anonymous` from `registry:2`, `manager-not-active`, repositories limited to the workflow) and `workflow-capabilities`; FR-021, FR-024. |
| Contracts tests | The two new schema families and their examples; the OpenAPI snapshot contains both operations. |
| End-to-end test `DockerRuntimeEndToEndTests` | The test Analyst image is built, pushed to `registry:2`, imported and approved; the real Manager core on `docker-desktop` with `unix:///var/run/docker.sock` claims the job, runs one restricted container, and the platform accepts exactly one result; afterwards no labelled container and no attempt directory remain (SC-001, SC-002). Failure variants (non-zero exit, missing manifest, out-of-memory, timeout, revocation) and a restart after a killed Manager with a leftover container, next to an unlabelled container that stays untouched (SC-003, SC-004). |

Expected: every suite passes; the end-to-end test fails visibly when Docker is
unavailable instead of skipping.

## Windows (manual, recorded evidence)

A person runs these steps on Windows; the implementation on GitHub cannot.
Until the evidence exists, `runtime-compatibility.json` carries no
`docker-desktop` entry, so a Windows Manager stays `Runtime unavailable`, and
the architecture states that the profile awaits manual verification.

On a Windows host with Docker Desktop (WSL2 backend, Linux containers):

1. Configure a development Manager with `Runtime:Profile=docker-desktop`
   (default endpoint `npipe://./pipe/docker_engine`) against a local AppHost.
2. Stop Docker Desktop and start the Manager: the tray window and
   `SocAlytics.Analysts.Manager status` show the failed endpoint check with
   remediation; the Manager is `Runtime unavailable` (SC-005).
3. Start Docker Desktop: preflight passes, the status lists the runtime
   version, the offered capabilities, and the image cache use.
4. Run an analysis with the test Analyst image: one container runs with the
   restrictions of [contracts/analyst-container.md](contracts/analyst-container.md)
   (check with `docker inspect`), the result is accepted, and the container is
   removed.
5. In a separate change, record the Docker Desktop and engine versions,
   Windows version, and the outcome in
   `docs/architecture/analyst-runtime-and-recovery.md`, and add the verified
   range to `runtime-compatibility.json`. For steps 1–4 before that change, a
   development build may load the entry from a local compatibility file.

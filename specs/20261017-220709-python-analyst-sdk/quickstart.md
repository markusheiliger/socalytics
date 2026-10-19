# Quickstart: Python Analyst SDK

Validation guide for the implemented feature. Details: [data-model.md](data-model.md),
[contracts/](contracts/), [research.md](research.md).

## Prerequisites

- The environment of [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md):
  CPython 3.12.15 and `uv` 0.13.1 (on Linux CI from `environment-setup`; locally any CPython 3.12 with `uv`).
- The .NET 10 SDK and a Docker-compatible runtime for the platform and Manager suites (see `AGENTS.md`).
- Fact Indexing and Partial-Coverage Match Barriers merged, so `contracts/analysis/fact-artifact/v1/` and the 1.1.0
  result-manifest and upstream-results schemas exist.

## 1. SDK: locked build and tests

From `src/analysts/sdk/`, the environment's exact command sequence
([environment-interface.md](../archive/20261017-163801-analyst-media-environment/contracts/environment-interface.md)):

```text
UV_PYTHON_DOWNLOADS=never
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

On Windows use `.venv\Scripts\python.exe` for the build. Expected: the wheel contains `socalytics_analyst/contracts/`;
every test passes, including:

- contract conformance (every valid and invalid example of analyst-job, analyst-attempt, result-manifest,
  upstream-results, fact-artifact; the copied-schema identity test; the model/schema field comparison);
- result writing for segment and match jobs with and without upstream dependencies, validated against the canonical
  schemas and compared field by field with the job and attempt context;
- refusals (fact outside the window, unexpected result file, second finalization, invalid artifact names);
- staged-input verification failures (changed byte, missing or extra staged result, checksum mismatch);
- the entry point's exit statuses 0, 1, and 2, the absence of `manifest.json` after failures, and the single output
  lines.

## 2. Copied schemas stay identical

```text
uv run --no-sync python scripts/sync_contracts.py --check
```

Expected: no differences. After a canonical contract change, run it without `--check`, rerun the tests, and commit
the refreshed copy with the contract change.

## 3. Platform and Manager

```powershell
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/SocAlytics.Platform.slnx --no-build
dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx --no-build
```

Expected: the analyst-job 1.1.0 and analyst-attempt examples pass; newly published segment jobs carry `workflow` and a
`segmentWindow` equal to what acceptance compares, match jobs carry `workflow` only; the Manager's `job.json` is
byte-identical to the delivered message and `attempt.json` names the claimed attempt on the simulated and Docker
runtimes; existing end-to-end tests pass unchanged.

## 4. Author walkthrough (manual)

Create a scratch directory with `job.json` from `contracts/analysis/analyst-job/v1/examples/valid/segment-job-with-window.json`,
`attempt.json` from `contracts/analysis/analyst-attempt/v1/examples/valid/attempt.json`, and empty `result/` and
`scratch/` folders; run the README's minimal example with `root` pointing at it. Expected: exit 0, `result/` holds
exactly `facts.json` and `manifest.json`, and the manifest validates against the canonical schema.

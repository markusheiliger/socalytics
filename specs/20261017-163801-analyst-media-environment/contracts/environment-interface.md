# Environment Interface Contract

This documents build-environment expectations, not a public product API or a new machine-readable root contract.

## Existing Composite Action Interface

- Preserve setup inputs `workspace` and `install-tools`, all existing outputs, and existing trusted cache behavior.
- Preserve verify inputs `workspace`, `mode`, `changed-files`, and `log-file`, and outputs `checks` and `uncovered`.
- The existing caller provides Node.js, the Linux runner, and its container runtime. Do not modify workflows.
- New executable setup and media smoke checks run only when `install-tools` is true.
- New verified tool paths may be published through the existing job path and environment files. `SOCALYTICS_PYTHON` names the exact installed interpreter; it is not a new action output.
- Failure must retain established logs and check/uncovered output semantics. Do not introduce a success-shaped result or swallow a failed stage.

## Python Product Convention

Recognized independent project manifests:

```text
src/analysts/sdk/pyproject.toml
src/analysts/capabilities/<name>/pyproject.toml
```

Each recognized project must supply:

- A unique normalized `[project].name` and `requires-python` compatible with the installed interpreter.
- Its own committed `uv.lock`, consistent with its manifest.
- A package build definition, with the backends required by this project and its local dependencies included in locked development dependencies.
- Compatible published wheels for external dependencies; this initial environment does not build external source distributions.
- pytest in locked development dependencies and at least one collectable test.
- Local Python dependencies, where present, in project requirements and path-based `[tool.uv.sources]` declarations pointing to recognized projects.

No shared uv workspace or implicit alternate interpreter is supported. A local dependency outside the recognized roots, an absent dependency project, a cycle, or duplicate project identity fails explicitly. An unrecognized product manifest must not silently disappear from finalize verification.

The action creates no project or lockfile. The SDK and later capability specs implement this convention when their first meaningful artifacts are added.

## Verification Command Contract

Within each selected project, use the provisioned interpreter and disable Python downloads:

```text
UV_PYTHON_DOWNLOADS=never
UV_PYTHON=<SOCALYTICS_PYTHON>
uv sync --locked --all-groups --no-install-local --no-build
uv sync --locked --all-groups --no-build-isolation
uv build --wheel --no-build-isolation --python .venv/bin/python
uv run --no-sync pytest
```

The environment variable lines express the subprocess environment, not shell commands. The environment owns the exact command sequence rather than accepting arbitrary manifest-defined shell commands. Build backends and pytest execute only inside the existing read-only verification job.

The bootstrap sync installs locked external wheels and backend dependencies without building local packages or external source distributions. The second sync installs the root and local dependency packages using those already locked backends, without isolated dependency resolution. The explicit no-isolation wheel build uses that same environment, not a fresh resolver. Missing or stale locks, incompatible runtime declarations, unavailable external wheels, missing backend dependencies, failed builds, pytest failures, and pytest's no-tests exit all fail the project check. Committed dependency declarations must remain unchanged.

## Selection Contract

| Condition | Selected Python projects | Source coverage |
| --- | --- | --- |
| No recognized projects | None | No added Python scope |
| Own project changes in task or phase | That project and reverse local-dependency consumers | Existing recognized project roots only |
| Analysis/common contract changes | All existing recognized projects | Contracts stay covered through the platform |
| Environment action folder changes | All existing recognized projects | No blanket source coverage |
| Finalize mode | All existing recognized projects | Existing recognized project roots only |
| Deleted or missing manifest | No check can approve that deleted project | Its source remains uncovered |
| Unrelated changes | No additional Python project check | Existing action rules remain authoritative |

Match changed paths by complete directory components. Never match a similarly prefixed sibling accidentally. The Manager's solution and its contracts retain their existing check and ownership.

## Media And Installation Contract

All new pins come from the setup action's toolchain JSON. Verify archives against the committed hashes before safe extraction, assert executable identity/version, preserve notices, and fail explicitly for unsupported hosts or missing tools.

The synthetic smoke check creates a two-second, 64 by 64 MP4 using native MPEG-4 encoding, extracts a nonempty keyframe-compatible interval, probes a finite positive duration and video stream, and decodes the extracted media. Use temporary storage and bounded output; delete only the check's own fixture directory.

No external recording, model, credential, H.264 encoding dependency, service, or additional tool download is required by this smoke check.

## Failure Guidance Contract

Use one clear recommendation per failure:

- Download failure: restore access to the named exact archive and rerun setup; a permanently unavailable artifact requires a reviewed environment pin update.
- Digest or version mismatch: inspect the named artifact/pin evidence and correct the environment through a standalone change; never bypass the check.
- Project convention or runtime incompatibility: correct the named manifest/dependency declaration to the documented contract, or author the required environment extension before retrying.
- Stale/missing lock: correct and commit the consuming project's dependency declarations before retrying; verification does not repair them.
- Build or test failure: reproduce the named failed command in that project, correct the source failure, and retry verification.

Do not post pull request comments or change workflow handoff logic from these environment actions.

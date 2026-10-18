# Research: Analyst and Media Verification Environment

**Date**: 2026-10-17

## Decision 1 - Explicitly pinned, verified Linux x64 archives

**Decision**: Use the following exact public release artifacts. Store their pins once in the setup action's future toolchain JSON; reject a download whose digest differs before extracting or executing it.

| Tool | Exact artifact | SHA-256 |
| --- | --- | --- |
| CPython 3.12.15 | `cpython-3.12.15+20261013-x86_64-unknown-linux-gnu-install_only.tar.gz`, python-build-standalone release `20261013` | `5d546fc004fc2435fa142eb8f42ed8de5eb4725963dc22f7e6671014a80eb560` |
| uv 0.13.1 | `uv-x86_64-unknown-linux-gnu.tar.gz`, release `0.13.1` | `eadd9d27c134ab5ac1ffd4473b97a741407f3ea238288f03121fc4deddf90220` |
| FFmpeg and ffprobe | `ffmpeg-n8.1.3-9-g29e619e767-linux64-lgpl-8.1.tar.xz`, release `autobuild-2026-09-30-13-08` | `dfa863a00ca81f1bdf58a372b18cff4820f0017e55de32778de8ecd8ed92a02e` |

**Rationale**: Exact artifacts avoid relying on runner-installed versions, future project files, mutable installation scripts, or additional setup actions. The 3.12 baseline is deliberately conservative for the first SDK; this is not a claim that every future native model dependency supports it.

**Verified public metadata**:

- [Python release and assets](https://api.github.com/repos/astral-sh/python-build-standalone/releases/tags/20261013): the selected asset and full SHA-256 exist; the release reports `immutable: true`.
- [uv release and assets](https://api.github.com/repos/astral-sh/uv/releases/tags/0.13.1): the selected asset and full SHA-256 exist; the release reports `immutable: true`.
- [FFmpeg monthly release and assets](https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/autobuild-2026-09-30-13-08): the selected LGPL asset and full SHA-256 exist; the release reports `immutable: false`. Its dated tag is not an immutability guarantee; the committed hash is the integrity boundary.

**Alternatives considered**:

- CPython 3.12.12: a real published release, but superseded by the verified 3.12.15 artifact available at authoring.
- Runtime installation from project declarations: unsuitable before the project exists and permits an unexpected download during verification.
- Distribution packages or floating `latest` URLs: insufficient control over the exact executable build.
- Building CPython or FFmpeg from source in every job: unnecessary setup cost and build-tool expansion for this environment.

## Decision 2 - Retained LGPL media tooling, not an expiring GPL daily build

**Decision**: Use the September month-end BtbN LGPL release-branch build, not the initially researched October daily GPL archive.

**Rationale**: The [publisher's documented retention policy](https://github.com/BtbN/FFmpeg-Builds#release-retention-policy) keeps the last build of each month for two years, but only the most recent fourteen daily builds. A daily pin would soon become undownloadable. The LGPL variant omits GPL-only libraries such as x264/x265; native MPEG-4 synthetic fixtures are sufficient for this feature's extraction, probe, and decode smoke check.

Preserve publisher and component license notices, record the distribution and build revision, and fail explicitly if the pinned archive disappears or changes. This selection does not approve patents, production codec coverage, product-image redistribution, or the Segment Service implementation.

**Alternatives considered**: A daily GPL build is both unnecessarily broad for this smoke check and short-lived. Distribution FFmpeg packages do not provide this exact archive pin. A production media image belongs to a later feature.

## Decision 3 - Independently locked SDK and capability projects

**Decision**: Recognize an SDK project at `src/analysts/sdk/` and capability projects immediately under `src/analysts/capabilities/`, each with its own manifest and lockfile. Declare local Python dependencies with project requirements and path entries in `[tool.uv.sources]`.

**Rationale**: This provides a small, predictable contract before products exist, permits capability-specific locked dependencies, and keeps the Manager out of Python coverage. The selector needs only the Python standard library, existing changed-path input, and a local dependency graph. An SDK change must reverify its consumers.

**Alternatives considered**: A broad Analysts-area coverage rule hides unowned files. A shared uv workspace would bind otherwise independent runtime environments and is unnecessary for the first SDK. Arbitrary recursive manifest discovery risks promoting dependency or generated directories into product ownership.

## Decision 4 - Locked build and test execution

**Decision**: Disable Python downloads. Bootstrap the project's locked external and development dependencies with `--no-install-local --no-build`, then synchronize local packages with `--no-build-isolation`. Build with the already installed locked build backend and no build isolation, then run pytest without synchronization.

**Rationale**: [uv's locked synchronization](https://docs.astral.sh/uv/concepts/projects/sync/) refuses stale declarations. [Python download controls](https://docs.astral.sh/uv/concepts/python-versions/) prevent runtime substitution. [Build documentation](https://docs.astral.sh/uv/concepts/projects/build/) makes clear that default isolated builds can resolve build dependencies independently; no-isolation builds instead require the backend to be included in the project's locked environment.

Future projects must lock pytest and the build backends required by themselves and their local dependencies in their development dependencies. External dependencies must supply compatible wheels for this initial environment; building external source distributions needs a separate environment extension, not an unlocked fallback. Missing backends, missing tests, unsupported project layouts, dependency cycles, and command failures are explicit errors.

**Alternatives considered**: A plain `uv sync` can build local packages in independently resolved isolated environments before the explicit build even starts. A plain isolated `uv build` has the same risk. The two-stage sync uses documented [installation controls](https://docs.astral.sh/uv/concepts/projects/sync/) without adding a separate resolver or wheel-management layer. `uv run` without `--no-sync` may update the environment again. Automatically fixing a stale lockfile during verification hides an incomplete change.

## Evidence Boundary

Release metadata, full archive hashes, retention policy, and tool documentation were checked during authoring. No tool archive was installed, and no build, smoke check, or model compatibility test was executed in this planning step. The implementation must verify archive contents, installed versions, real commands, fixture tests, and Linux environment self-verification before this environment feature can merge.

No planning question remains unresolved. Actual runtime acceptance is a required implementation gate, not assumed research evidence.

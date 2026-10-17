# Environment Setup Action

This composite action prepares the build and verification environment of the Spec Kit workflows. This document covers the Analyst Python and media toolchain; the other installed tools (.NET, Markdown linters, SoftHSM2, Kiota, Playwright Chromium) are described in the comments of [`action.yml`](action.yml).

The toolchain is build-environment tooling only. It is not a product dependency, and nothing here approves a distribution for product use, product-image redistribution, patent coverage, production codecs, or production readiness.

## Pinned Distributions

[`analyst-media-toolchain.json`](analyst-media-toolchain.json) is the only source of these pins. Changing any entry requires a standalone environment change.

| Tool | Version | Publisher release | Archive |
| --- | --- | --- | --- |
| CPython | 3.12.15 | `astral-sh/python-build-standalone` `20261013` (immutable release) | `install_only` `x86_64-unknown-linux-gnu` `tar.gz` |
| uv | 0.13.1 | `astral-sh/uv` `0.13.1` (immutable release) | `x86_64-unknown-linux-gnu` `tar.gz` |
| FFmpeg and ffprobe (LGPL build) | `n8.1.3-9-g29e619e767` | `BtbN/FFmpeg-Builds` `autobuild-2026-09-30-13-08` (not immutable) | `linux64-lgpl-8.1` `tar.xz` |

Setup installs only these distributions. A different CPython, uv, FFmpeg, or ffprobe on the runner never satisfies setup, and no Python project file is read to select them.

## Scope

- Supported host: Linux x64 (`Linux` and `x86_64`). Any other host fails with the detected and the supported host named.
- The installer needs `jq`, `curl`, `tar`, `sha256sum`, and `timeout` from the runner image and fails naming the missing program.
- The FFmpeg build is the LGPL variant. It has no GPL-only libraries such as x264 or x265; the smoke check uses native MPEG-4 encoding.

## What Setup Does

When `install-tools` is `true` (the default), the step `Install the Analyst Python and media toolchain` runs [`install-analyst-media.sh`](install-analyst-media.sh), which for each tool in declaration order:

1. Downloads the exact archive named in the declaration.
2. Compares the archive's SHA-256 digest with the committed value before anything is listed or extracted.
3. Rejects archives with absolute paths, entries or links that escape the extraction directory, or entries outside the declared root.
4. Extracts into a staging directory, requires the declared root and executables, and moves the root into the install directory (`$RUNNER_TEMP/analyst-media-tools`).
5. Runs the declared version check and requires the expected output (`Python 3.12.15`, `uv 0.13.1`, and the FFmpeg and ffprobe `n8.1.3-9-g29e619e767` banners).
6. Publishes the verified tool directories through `GITHUB_PATH` and `SOCALYTICS_PYTHON`, the exact installed interpreter, through `GITHUB_ENV`. `SOCALYTICS_PYTHON` is not an action output; the existing inputs and outputs are unchanged.

Downloaded archives and staging directories are deleted after a successful run.

## Artifact Integrity And Retention

- The committed SHA-256 digest is the integrity boundary for every archive. A mismatch fails setup before extraction and is never bypassed.
- The CPython and uv releases are immutable. The FFmpeg release is not: its dated tag is no immutability guarantee, so only the digest protects against a changed archive.
- The FFmpeg publisher keeps the last build of each month for two years and only the most recent fourteen daily builds ([release retention policy](https://github.com/BtbN/FFmpeg-Builds#release-retention-policy)). The pinned monthly build can therefore disappear from the publisher; setup then fails at the download stage until a reviewed environment pin update replaces it.
- Archives are fetched from the publisher on each run. This action adds no download cache for them.

## Notices And Provenance

Beside the installed tools, in `$RUNNER_TEMP/analyst-media-tools/notices/<tool>/`, setup keeps:

- `PROVENANCE.txt` with the tool name, version, provenance location, archive file name, and SHA-256 digest.
- The license files named by the declaration: the CPython `LICENSE.txt` and the FFmpeg `LICENSE.txt`. The uv declaration names no license file inside its archive, so only its provenance is kept.

A declared license file missing from an archive fails setup.

## Tools-Disabled Behavior

With `install-tools: "false"` (only the trusted cache population job), the toolchain installation and the synthetic smoke check are skipped together with the other executable tool installs. Nothing is downloaded, no tool directory is added to `GITHUB_PATH`, and `SOCALYTICS_PYTHON` stays unset. Cache keys, cache restores, and outputs behave as before.

## Synthetic Smoke Check

After FFmpeg and ffprobe pass their version checks, setup creates a temporary fixture directory inside the install directory and, using only the verified tools:

1. Creates a two-second, 64 by 64 MPEG-4 MP4 from the built-in `testsrc2` source.
2. Extracts a nonempty interval by stream copy, starting at a keyframe-compatible offset.
3. Probes the extracted file and requires a video stream and a finite positive duration.
4. Decodes the extracted file.

The check needs no recording, model, credential, service, H.264 encoder, or additional download. Each command has a 60-second limit and its reported output is truncated. Only the check's own fixture directory is deleted, also when the check fails.

## Failure And Recovery

Every failure prints one line naming the tool, the failed stage, and the cause, followed by one `Recommended action`, and stops setup. No stage is skipped or reported as success.

| Stage | One action |
| --- | --- |
| `download` | Restore access to the named exact archive and rerun setup; a permanently unavailable artifact requires a reviewed environment pin update. |
| `integrity`, `version`, `smoke` | Inspect the named artifact and pin evidence and correct the environment through a standalone environment change; never bypass the check. |
| `extract`, `layout`, `notices` | Inspect the named archive and its layout and correct the pin, root, executables, or license locations through a standalone environment change. |
| `host`, `prerequisites` | Run setup on the supported Linux x64 runner image. |
| `declaration`, `toolchain` | Correct `analyst-media-toolchain.json` through a standalone environment change. |

## Changing Or Testing The Toolchain

Pin changes go through a standalone environment spec that changes only the two action folders. [`toolchain.test.mjs`](toolchain.test.mjs) tests the declaration and the installer without downloading the real archives; run it with `node --test .github/actions/environment-setup/toolchain.test.mjs`.

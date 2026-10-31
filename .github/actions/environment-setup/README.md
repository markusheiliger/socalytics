# Environment Setup Action

This composite action prepares the build and verification environment of the Spec Kit workflows. This document covers the Analyst Python and media toolchain and the Analyst computer-vision and inference libraries; the other installed tools (.NET, Markdown linters, SoftHSM2, Kiota, Playwright Chromium) are described in the comments of [`action.yml`](action.yml).

The toolchain is build-environment tooling only. It is not a product dependency, and nothing here approves a distribution for product use, product-image redistribution, patent coverage, production codecs, or production readiness.

## Pinned Distributions

[`analyst-media-toolchain.json`](analyst-media-toolchain.json) is the only source of these pins. Changing any entry requires a standalone environment change.

| Tool | Version | Publisher release | Archive |
| --- | --- | --- | --- |
| CPython | 3.12.15 | `astral-sh/python-build-standalone` `20261013` (immutable release) | `install_only` `x86_64-unknown-linux-gnu` `tar.gz` |
| uv | 0.13.1 | `astral-sh/uv` `0.13.1` (immutable release) | `x86_64-unknown-linux-gnu` `tar.gz` |
| FFmpeg and ffprobe (LGPL build) | `n8.1.3-9-g29e619e767` | `BtbN/FFmpeg-Builds` `autobuild-2026-09-30-13-08` (not immutable) | `linux64-lgpl-8.1` `tar.xz` |

Setup installs only these distributions. A different CPython, uv, FFmpeg, or ffprobe on the runner never satisfies setup, and no Python project file is read to select them.

## Agent Python Projects

The installed `SOCALYTICS_PYTHON` and `uv` also serve the agent Python projects directly under `src/agents/<name>/` that [`environment-verify`](../environment-verify/README.md) checks. Setup installs nothing additional for them and reads no agent project file.

## Scope

- Supported host: Linux x64 (`Linux` and `x86_64`). Any other host fails with the detected and the supported host named.
- The installer needs `jq`, `curl`, `tar`, `sha256sum`, and `timeout` from the runner image and fails naming the missing program.
- The FFmpeg build is the LGPL variant. It has no GPL-only libraries such as x264 or x265; the smoke check uses native MPEG-4 encoding.

## What Setup Does

When `install-tools` is `true` (the default), the step `Start the Analyst Python and media toolchain install` starts [`install-analyst-media.sh`](install-analyst-media.sh) in the background right after the dependency cache restores, so the downloads overlap the Markdown linter, SoftHSM2, Kiota, and Playwright installs. The last step, `Install the Analyst Python and media toolchain`, waits for it (at most 20 minutes), prints its log, fails with its exit code (also when it was never started), and only then publishes its `GITHUB_PATH` and `GITHUB_ENV` additions. The installer itself is unchanged and, for each tool in declaration order:

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

## Analyst Computer-Vision And Inference Libraries

After the toolchain step and only when `install-tools` is `true`, the step `Install the Analyst computer-vision libraries` runs [`install-analyst-cv.sh`](install-analyst-cv.sh). It provisions CPU-only wheels for the Analyst capabilities that decode video frames and run model inference, and it needs the toolchain step's `SOCALYTICS_PYTHON`, `uv`, and `ffmpeg`. Like the toolchain, it is build-environment tooling: it approves no library for product use, product-image redistribution, patent coverage, or production, and it sets no production value (library choice, versions, licence policy, and runtime settings for production remain unresolved).

### Pinned Libraries

[`analyst-cv-libraries.json`](analyst-cv-libraries.json) is the single declaration of the pins and of every rule applied to them: exact wheel versions, file names, URLs, SHA-256 digests, publisher, licence expression, and licence text location, plus the denied packages, the licence allow-list, the LGPL and runtime-exception licences, the denied library prefixes, and the `licenseTable` of bundled shared libraries. Changing any entry requires a standalone environment change. It pins six wheels for Linux x86_64 and CPython 3.12 (`cp312`):

| Wheel | Version | Role |
| --- | --- | --- |
| `opencv-python-headless` | 5.0.0.93 | library |
| `numpy` | 2.5.4 | library |
| `onnxruntime` | 1.31.0 | library |
| `flatbuffers` | 25.12.19 | dependency |
| `packaging` | 26.3 | dependency |
| `protobuf` | 7.36.2 | dependency |

The three dependency wheels are the closure the libraries need, because the install resolves no dependencies. [`environment-verify`](../environment-verify/README.md) reads the same declaration to check project locks against the pins.

### Download, Integrity, And Retention

1. Every wheel is downloaded from its declared https URL (`cv download`).
2. Each file's SHA-256 digest is compared with the committed value before anything is opened (`cv integrity`); a mismatch is never bypassed.
3. Each file name must be a binary wheel named and versioned as declared, with tags that match the declared interpreter tag and the host (`cv compatibility`); source distributions are never built.
4. The licence and SBOM check runs on the downloaded wheels (`cv licenses`, below).
5. The verified wheels are copied into the wheelhouse `analyst-cv-wheels/` of the install directory with a hash-pinned `requirements.txt`, and installed offline, with hashes required, without dependency resolution, and without an index into the isolated smoke environment `analyst-cv-venv/` (`cv install`). Nothing is installed into the toolchain interpreter, and `GITHUB_PATH` and `GITHUB_ENV` are unchanged.
6. The synthetic smoke check runs in that environment (`cv smoke`): the pinned FFmpeg writes a two-second 64 by 64 MPEG-4 clip, OpenCV decodes its frames, NumPy computes a mean, and ONNX Runtime executes a tiny `Add` graph built in memory on the CPU. It needs no recording, model weights, dataset, `onnx` package, or additional download, and it deletes only its own temporary directory. Each run limits the check to 300 seconds.

The downloads directory is deleted when the script ends, also on failure. The wheelhouse and the smoke environment stay in the install directory for the rest of the job, which is the only retention. Wheels are fetched from the publisher on each run, and this action adds no cache for them.

### Licence And SBOM Rule

[`analyst-cv-licenses.py`](analyst-cv-licenses.py) (standard library only) checks every wheel and writes the SBOM before anything is installed:

- **Allow-list**: every SPDX licence leaf of a wheel expression must be on `licenseAllowList`; an `OR` needs one acceptable alternative and an `AND` needs all of them. GPL, AGPL, and SSPL families are denied, unrecognised identifiers are unknown, and recognised but not allow-listed ones are not allow-listed.
- **LGPL rule**: an LGPL licence (and the declared GCC runtime exception) is accepted only for a bundled shared library whose row declares `linkage: dynamic`, that is a distinct ELF shared object with a `DT_SONAME`, and that another ELF in the wheel lists in `DT_NEEDED`. A wheel's own licence can never use this exception.
- **Denied libraries**: a bundled library matching a denied prefix (`libx264`, `libx265`, `libxvid`, `libvidstab`, `libfdk`) fails, as does a bundled library without a `licenseTable` row.
- **Evidence versus table**: the `licenseTable` row of each wheel and bundled library must match the declaration, name a licence text that the wheel ships, and agree with the wheel's `METADATA` licence evidence. A row for a library the wheel does not bundle is an `evidence-gap`.
- **FFmpeg**: the pinned FFmpeg build of the toolchain declaration must appear in the SBOM.

Findings print one `<wheel|library>: <kind>: <problem>` line each, with the kinds `denied-license`, `unknown-license`, `not-allow-listed`, `evidence-mismatch`, `table-gap`, `evidence-gap`, `denied-library`, `lgpl-not-distinct`, `lgpl-not-dynamic`, `not-in-sbom`, and `missing-digest`. Setup reports the first one as the failure and counts the rest.

The CycloneDX 1.6 SBOM is written to `notices/analyst-cv/sbom.cdx.json` in the install directory. Beside it, `notices/analyst-cv/<wheel file name>/` holds each wheel's `PROVENANCE.txt` (URL, digest, publisher, licence, role, and the digest of every bundled library) and the licence texts. On success, setup prints the last complete valid `Verified synthetic CV smoke check: ...` evidence line from standard output; diagnostics on standard error do not interfere. It also prints `Verified Analyst CV libraries: <n> wheels, <m> bundled libraries, SBOM <path>`.

### Excluded Material

The set contains no GUI or non-headless OpenCV variant (`opencv-python`, `opencv-contrib-python`), no GPU runtime of any vendor (`onnxruntime-gpu`, `onnxruntime-*` variants), no CUDA, TensorRT, `nvidia-*`, or `triton` package, no accelerator-specific runtime, no GPL codec libraries, and no model weights or datasets. `deniedPackages` lists the excluded package names, which [`environment-verify`](../environment-verify/README.md) rejects in any project lock. GPU, accelerator, model, and dataset provisioning remain out of scope.

### CV Tools-Disabled Behavior

With `install-tools: "false"`, this step is skipped with the toolchain: nothing is downloaded, no environment is created, and no SBOM is written. Cache keys, cache restores, and outputs behave as before.

### CV Failure And Recovery

A failure prints `ERROR: Analyst CV libraries: library=<name> stage=<stage>: <problem>` followed by one `Recommended action`, and stops setup. The stages, in order, are `prerequisites`, `declaration`, `host platform`, `cv download`, `cv integrity`, `cv compatibility`, `cv licenses`, `cv install`, and `cv smoke`.

| Stage | One action |
| --- | --- |
| `cv download` | Restore access to the named exact wheel and rerun setup; a permanently unavailable artifact requires a reviewed environment pin update. |
| `cv integrity`, `cv compatibility`, `cv install`, `cv smoke` | Inspect the named artifact and pin evidence and correct the environment through a standalone environment change; never bypass the check. |
| `cv licenses` | Correct the pin or the licence table through a standalone environment change and rerun setup; never bypass the check. |
| `host platform` | Run setup on the supported Linux x64 runner image. |
| `declaration` | Correct `analyst-cv-libraries.json` through a standalone environment change and rerun setup. |
| `prerequisites` | Run the Analyst toolchain step first and use the supported runner image, which provides the required programs. |

These stages indicate a broken environment, not a failing consuming project; a project whose lock disagrees with the pins fails the `library pin` stage of `environment-verify` instead.

## Changing Or Testing The Toolchain

Pin changes go through a standalone environment spec that changes only the two action folders. [`toolchain.test.mjs`](toolchain.test.mjs) tests the declaration and the installer without downloading the real archives; run it with `node --test .github/actions/environment-setup/toolchain.test.mjs`. [`cv-libraries.test.mjs`](cv-libraries.test.mjs) tests the library declaration, installer, licence check, and smoke check the same way; run it with `node --test .github/actions/environment-setup/cv-libraries.test.mjs`.

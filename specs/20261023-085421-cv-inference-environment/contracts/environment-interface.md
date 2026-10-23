# Environment Interface Extension: CV and Inference Libraries

This contract extends the [environment interface](../../archive/20261017-163801-analyst-media-environment/contracts/environment-interface.md)
of the Analyst and Media Verification Environment. Everything it does not mention is unchanged, including every input
and output of both actions.

## environment-setup

- **Step**: `Install the Analyst computer-vision libraries`, after the toolchain step, only when `install-tools` is
  `true`. In tools-disabled mode nothing is downloaded.
- **Script**: `bash "$GITHUB_ACTION_PATH/install-analyst-cv.sh"`.
- **Environment** (tests and the action): `ANALYST_CV_LIBRARIES` (declaration, default next to the script),
  `ANALYST_MEDIA_TOOLCHAIN`, `ANALYST_MEDIA_INSTALL_ROOT`, `ANALYST_MEDIA_HOST_OS`, `ANALYST_MEDIA_HOST_ARCH`
  (as for the toolchain script), `SOCALYTICS_PYTHON`, and `uv` and `ffmpeg` on `PATH`.
- **Stages**, in order: `prerequisites`, `declaration`, `host platform`, `cv download`, `cv integrity`,
  `cv compatibility`, `cv licenses`, `cv install`, `cv smoke`.
- **Failure line** (stderr, exit 1):
  `ERROR: Analyst CV libraries: library=<name|tool> stage=<stage>: <problem>` then
  `Recommended action: <one action>`.
- **Success evidence**: `Verified Analyst CV libraries: <n> wheels, <m> bundled libraries, SBOM <path>` and
  `Verified synthetic CV smoke check: decoded <frames> frames, NumPy mean <value>, ONNX Runtime Add graph executed`.
- **Files**: `notices/analyst-cv/sbom.cdx.json`, `notices/analyst-cv/<wheel>/PROVENANCE.txt` and licence files.
  No `GITHUB_PATH` or `GITHUB_ENV` change.

### Licence check

`python analyst-cv-licenses.py --declaration <file> --wheels <dir> --sbom <file> [--ffmpeg-toolchain <file>]` exits 0
and writes the SBOM, or exits 1 printing one line per finding
`<wheel|library>: <kind>: <problem>` where `kind` is `denied-license`, `unknown-license`, `not-allow-listed`,
`evidence-mismatch`, `table-gap`, `evidence-gap`, `denied-library`, `lgpl-not-distinct`, `lgpl-not-dynamic`,
`not-in-sbom`, or `missing-digest`.

## environment-verify

- **Stage** `library pin`, first stage of `python_project` for every selected Analyst Python project, run with
  `analyst-library-pins.py --declaration <file> --lock uv.lock --project <root>`. It never writes. It enforces the
  whole pinned closure (including `role: dependency` wheels) only when the project's lock contains at least one
  `role: library` wheel (`opencv-python-headless`, `numpy`, `onnxruntime`); `disallowed-package` applies to every
  project.
- **Findings**, one line each, ending the project's sequence:
  `project <root>: library pin: <library>: <kind>: locked <value>, pinned <value>. Recommended action: <action>`
  with `kind` one of `version`, `platform-tag`, `digest`, `source-distribution`, `missing-wheel`,
  `disallowed-package`.
- **Failure propagation**: through the existing `Analyst Python stage failed: library pin in <root> ...` line and
  `status=1`; later checks and outputs follow the existing action contract.
- **Coverage**: unchanged. `^src/analysts/capabilities/<name>/` is covered only while the project's `pyproject.toml`
  exists; no other path under `src/analysts/` is covered.
- **Environment tests**: `ENVIRONMENT_TESTS` gains `cv-libraries.test.mjs`.

## Recommended actions

- Broken environment (download, integrity, compatibility, licences, smoke): restore access or correct the pin through
  a standalone environment change and rerun setup; never bypass a check.
- Consuming project (library pin): align the project's `uv.lock` with the pinned library set, or remove the disallowed
  package, and retry verification; a change of a pin is a standalone environment change.

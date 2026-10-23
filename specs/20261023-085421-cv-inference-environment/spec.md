# Feature Specification: Computer-Vision and Inference Verification Environment

**Feature Branch**: `20261023-085421-cv-inference-environment`

**Created**: 2026-10-23

**Status**: Draft

**Input**: User description: "An environment-only spec so that Analyst capabilities that need computer-vision and
model-inference libraries can be built and verified by the Spec Kit automation. It changes nothing but the two composite
action folders `.github/actions/environment-setup` and `.github/actions/environment-verify` (plus its own `tasks.md`
ticks): it provisions the pinned, locked CPU-only Python libraries the architecture selects for the Analyst baselines
(OpenCV headless without GPL/AGPL-licensed codecs, NumPy, and ONNX Runtime CPU) next to the CPython, uv and pinned
FFmpeg/ffprobe toolchain that the Analyst and Media Verification Environment already provides, and extends
environment-verify to run the locked sync, build and pytest sequence for every project under `src/analysts/capabilities/`
with its paths added to `COVERED` only where the project exists. It states how a licence and software-bill-of-materials
check of the added wheels is verified, and installs no GPU runtime, TensorRT, model weights or datasets. Out of scope:
any Analyst capability, model, corpus, platform, Web UI or documentation change beyond the action folders; production
toolchain values."

## Clarifications

### Session 2026-10-23

- Q: Which licence families should the licence check accept as "permissive" for the added wheels and the native libraries bundled in them? → A: A fixed allow-list of SPDX identifiers kept in the pinned declaration (for example MIT, BSD-2/3-Clause, Apache-2.0, ISC, Zlib, PSF-2.0, MPL-2.0, and similar); anything else fails, so every change to the list is a standalone environment change.
- Q: What makes a bundled LGPL shared library count as "separately packaged" so the licence check accepts it? → A: It is a dynamically loaded shared object (.so) that is a distinct file inside the wheel (not statically linked into another binary), and it is recorded in the bill of materials with its licence name and the location of its licence text.
- Q: How does the licence check classify a bundled GPL library that carries a runtime exception (for example libgfortran under `GPL-3.0-or-later WITH GCC-exception-3.1`)? → A: The pinned declaration lists such licences in `runtimeExceptionLicenses`; one is accepted only for a bundled library that is a distinct dynamically loaded `.so`, recorded with its licence name, exception name, and licence-text location, exactly like the LGPL rule; every other GPL or AGPL licence still fails.
- Q: From what evidence does the licence check determine the licence of each wheel and each bundled native library? → A: Both wheel evidence and a curated table: the check cross-checks the shipped licence evidence against the committed table and fails on any disagreement or gap.

## User Scenarios & Testing *(mandatory)*

The users are contributors and the Spec Kit implementation automation. Their need is trustworthy, reproducible
verification of Analyst capabilities that decode video frames and run model inference, before the first such capability
(the Shot-Boundary Media Context Analyst) and every later learned-detector capability is implemented. The environment is
build-environment tooling only; it is not a product dependency and promotes no product library, model, or production
value.

### User Story 1 - Build and verify a capability that needs computer-vision libraries (Priority: P1)

An implementation agent works on an Analyst capability under `src/analysts/capabilities/<name>/` whose locked
dependencies include the headless OpenCV, NumPy, and ONNX Runtime CPU wheels. The automation provisions exactly those
pinned, hash-verified CPU-only wheels beside the existing toolchain, restores the project's locked dependencies, builds
its package, and runs its tests, without any download of an unpinned or unverified library and without building a
library from source.

**Why this priority**: Without this, the next real capability cannot pass verification, and its task would fail or hold
for review.

**Independent Test**: Run setup and the Analyst project check against an isolated fixture capability whose lock needs
the three libraries; confirm the check passes, imports each library, and decodes and processes a short synthetic frame
sequence and runs a tiny synthetic inference graph using only the provisioned libraries.

**Acceptance Scenarios**:

1. **Given** a fresh supported runner and a capability project whose locked dependencies name the pinned wheels,
   **When** setup and verification run, **Then** the wheels come from the pinned declaration with verified digests,
   the locked sync, wheel build, and tests complete, and the project's files are reported as covered.
2. **Given** a wheel whose download fails or whose digest differs from the pinned value, **When** setup or the locked
   sync runs, **Then** it fails naming the library and stage with one recommended action and never falls back to an
   unpinned, unlocked, or source-built alternative.
3. **Given** a capability whose lock requires a different version, platform tag, or GPU variant of these libraries,
   **When** it is verified, **Then** the check fails in the library pin stage that runs before the locked sync, naming
   the library, the locked value, and the pinned value.
4. **Given** setup runs with the existing tools-disabled mode, **When** it completes, **Then** no library is downloaded
   and the existing trusted cache-population contract is unchanged.
5. **Given** the provisioned libraries and the pinned FFmpeg and ffprobe, **When** a synthetic smoke check runs,
   **Then** the headless OpenCV library decodes a short synthetic clip, NumPy processes its frames, and ONNX Runtime
   CPU executes a tiny synthetic graph, with no recording, model weights, dataset, or additional download.

---

### User Story 2 - Check licences and the software bill of materials of the added wheels (Priority: P2)

A maintainer reviewing an environment change needs evidence that the added wheels, their transitive dependencies, and
the native libraries bundled inside them satisfy the architecture's licence rules, and that a software bill of
materials records them.

**Why this priority**: The architecture requires a recorded licence and provenance audit for every named framework and
transitive dependency, and the OpenCV distribution must carry no GPL- or AGPL-licensed codecs.

**Independent Test**: Run the licence and bill-of-materials check against fixture wheel sets: a conforming set passes
and produces a bill of materials; sets with a disallowed, unknown, or missing licence, a GPL- or AGPL-licensed bundled
codec library, or a missing digest each fail.

**Acceptance Scenarios**:

1. **Given** the pinned wheel set, **When** the check runs, **Then** it produces a software bill of materials listing
   each wheel, its version, digest, declared licence, publisher source, and the native shared libraries bundled in it
   with their licences, and every entry satisfies the accepted licence rule.
2. **Given** a wheel or bundled library with a GPL, AGPL, unknown, or missing licence (for example a bundled GPL-only
   codec library), **When** the check runs, **Then** it fails naming the wheel or library and the licence.
3. **Given** a bundled shared library under LGPL, **When** the check runs, **Then** it is accepted only if it is a
   dynamically loaded shared object (`.so`) that is a distinct file inside the wheel and not statically linked into
   another binary, and it is recorded with its licence name and licence text location; otherwise it fails.
4. **Given** a wheel or bundled library whose shipped licence evidence disagrees with the committed licence table, or
   that has no entry in the table or no shipped evidence, **When** the check runs, **Then** it fails naming the wheel or
   library and the disagreement or gap.
5. **Given** a licence identifier that is not on the fixed allow-list of permissive SPDX identifiers in the pinned
   declaration, **When** the check runs, **Then** it fails naming the wheel or library and the identifier.
6. **Given** a successful setup, **When** a maintainer inspects the install directory, **Then** the licence files and
   provenance records of the added wheels are kept beside the existing toolchain notices.

---

### User Story 3 - Verify every capability project and keep coverage honest (Priority: P3)

A maintainer reviews a verification result. Every project under `src/analysts/capabilities/` is verified by the locked
sync, build, and test sequence, and paths count as covered only for projects that exist.

**Why this priority**: Covered paths must reflect executed checks, and capability projects added later must not
silently pass.

**Independent Test**: Run selection and coverage against fixtures with no, one, and several capability projects
(including a deleted one), in all three verification modes.

**Acceptance Scenarios**:

1. **Given** several capability projects and a change to one, **When** verification runs in task or phase mode,
   **Then** that project and its dependents are checked; in finalize mode every capability project is checked.
2. **Given** a capability project that exists, **When** verification runs, **Then** its root is added to the covered
   paths; **Given** none exists or the project was deleted, **Then** no path under `src/analysts/capabilities/` is
   reported as covered.
3. **Given** a failing stage, **When** verification fails, **Then** the project, stage, and one recommended action are
   reported through the existing outputs, and later checks and outputs follow the existing action contract.
4. **Given** the existing platform, Manager, Web UI, SDK, Markdown, and mode-selection behavior, **When** this change
   lands, **Then** all existing environment tests pass unchanged and the action's inputs and outputs are unchanged.

### Edge Cases

- A runner has OpenCV, NumPy, or ONNX Runtime installed at another version or with a GUI, GPU, or GPL-codec variant:
  only the pinned wheels satisfy the provisioning.
- A project locks the non-headless OpenCV package, a GPU ONNX Runtime package, or a package that pulls TensorRT or CUDA
  runtimes: the check fails and names the disallowed package.
- A wheel is published only as a source distribution for the supported platform: the check fails instead of building it.
- A wheel index is unavailable or a pinned release was withdrawn: the stage fails explicitly; no mutable fallback is used.
- A transitive dependency of a pinned wheel has no recorded licence: the licence check fails.
- A licence is not on the allow-list, or the wheel's shipped licence evidence disagrees with the committed licence
  table or has no table entry: the licence check fails.
- A bundled LGPL library is statically linked into another binary or is not a distinct `.so` file in the wheel: the
  licence check fails.
- A capability project has no tests, a stale lock, or a manifest outside the recognized layout: it fails as the existing
  project convention already defines.
- A change touches only the capability projects' documentation: no extra Python check is triggered beyond existing
  selection rules.
- The pinned FFmpeg build and the FFmpeg libraries bundled in the OpenCV wheel differ: both are recorded in the bill
  of materials and neither replaces the other.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Implementation changes MUST be confined to `.github/actions/environment-setup/` and
  `.github/actions/environment-verify/`, apart from this feature's `tasks.md` completion markers. Tests, fixtures,
  declarations, and environment-local documentation MUST stay inside those folders. No Analyst capability, model,
  corpus, platform, Web UI, or other documentation file changes.
- **FR-002**: Setup MUST provision exactly pinned, hash-verified, CPU-only wheels of headless OpenCV, NumPy, and ONNX
  Runtime CPU (with the exact pinned transitive wheels they need) for the supported Linux x64 runner and the pinned
  CPython, next to the existing CPython, uv, and FFmpeg/ffprobe toolchain. The pins MUST live in one committed
  declaration that is the only source of these versions, and changing any entry MUST require a standalone environment
  change.
- **FR-003**: Provisioning MUST verify the digest of every wheel before it is made available, MUST accept only
  published binary wheels compatible with the pinned interpreter and host (never building a source distribution), and
  MUST fail on a download, integrity, compatibility, or version failure instead of accepting another installed or
  unpinned library.
- **FR-004**: The provisioned set MUST exclude GUI and non-headless OpenCV variants, GPU runtimes of any vendor, CUDA,
  TensorRT, accelerator-specific runtimes, model weights, and datasets. A project lock that names such a package MUST
  fail verification in the library pin stage that runs before the locked sync, naming the package.
- **FR-005**: Setup MUST keep the existing tools-disabled mode, caller-facing inputs and outputs, trusted cache
  boundaries, and the provision of every existing tool. The libraries MUST NOT be downloaded in tools-disabled mode,
  and no new download-cache infrastructure is added.
- **FR-006**: Setup MUST run a synthetic smoke check with only the provisioned tools and libraries that decodes a short
  synthetic clip with the headless OpenCV library, processes its frames with NumPy, and executes a tiny synthetic graph
  with ONNX Runtime CPU. It MUST need no recording, model weights, dataset, credential, service, or further download,
  MUST bound each command's time and reported output, and MUST delete only its own fixtures.
- **FR-007**: A locked verification of a capability project that depends on these libraries MUST resolve them from the
  pinned declaration (the same pinned digests) and MUST fail in the library pin stage that runs before the locked sync,
  naming the library, the locked value, and the pinned value, when the project's lock differs from the pin. The pin
  check enforces the whole closure (including `role: dependency` wheels) only when the project's lock contains at least
  one `role: library` wheel (`opencv-python-headless`, `numpy`, `onnxruntime`); the disallowed-package rule applies to
  every project.
- **FR-008**: Verification MUST run, for every project under `src/analysts/capabilities/` that exists, the locked sync,
  wheel build, and pytest sequence of the existing Analyst Python project check (preceded by the library pin stage of
  FR-007), in order and with the existing selection rules for task, phase, and finalize modes, shared contract changes,
  and local dependencies. Missing or stale
  locks, incompatible runtime requirements, failed builds, failed tests, and empty test runs MUST fail verification, and
  verification MUST NOT rewrite committed declarations.
- **FR-009**: Paths under `src/analysts/capabilities/<name>/` MUST be added to the covered paths only where that
  project exists and has an applicable executable check. Deleted or unrecognized projects, and any other path under
  `src/analysts/`, MUST remain uncovered.
- **FR-010**: The environment MUST define and run a licence and software-bill-of-materials check of the added wheels. It
  MUST record, for every wheel and every native shared library bundled in it, the name, version, digest, publisher
  source, declared licence, and licence-text location, in a software bill of materials in a standard exchange format
  kept beside the toolchain notices. It MUST accept a licence as permissive only if its SPDX identifier is on a fixed
  allow-list kept in the pinned declaration (for example MIT, BSD-2-Clause, BSD-3-Clause, Apache-2.0, ISC, Zlib,
  PSF-2.0, and MPL-2.0), so every change to the allow-list is a standalone environment change. It MUST accept LGPL only
  for a bundled library that is a dynamically loaded shared object (`.so`) and a distinct file inside the wheel (not
  statically linked into another binary), recorded with its licence name and licence-text location. It MUST accept a
  GPL licence with a runtime exception only if the licence is on the `runtimeExceptionLicenses` list of the pinned
  declaration (for example `GPL-3.0-or-later WITH GCC-exception-3.1`) and only for a bundled library that is a
  dynamically loaded `.so` and a distinct file inside the wheel, recorded with its licence name, exception name, and
  licence-text location, exactly like the LGPL rule; every other GPL or AGPL licence still fails. It MUST determine
  each wheel's and each bundled native library's licence from both the licence evidence shipped in the wheel and a
  curated licence table committed with the pinned declaration, cross-check the two, and fail on any disagreement or gap
  (an entry with no table row or no shipped evidence). It MUST fail on a GPL, AGPL, unknown, missing, or
  non-allow-listed licence, on a bundled GPL- or AGPL-licensed codec library, and on a wheel absent from the bill of
  materials.
- **FR-011**: Every failure MUST identify the library, project, or tool and the failed stage, propagate a failing status
  through the existing action contract, and give one concrete recommended action that distinguishes a broken
  environment from a failing consuming project and never recommends bypassing a check or weakening a test.
- **FR-012**: Existing platform, Manager, Web UI, Analyst SDK, Markdown, mode-selection, logging, output, and
  uncovered-file behavior MUST be preserved, and the existing environment tests MUST pass unchanged apart from
  assertions that this change explicitly extends.
- **FR-013**: Automated environment-local evidence MUST exercise: absent, present, deleted, and multiple capability
  projects; all three modes; wheel download, digest, compatibility, and version failures; disallowed-variant and
  source-distribution rejection; the smoke check passing and failing; licence and bill-of-materials pass and each
  failure kind; the tools-disabled mode; and preservation of the action interface. Evidence MUST use fixtures and
  stubbed external commands and MUST NOT depend on a product project.
- **FR-014**: The two action folders' environment-local documentation MUST state the pinned libraries, the integrity and
  retention behavior, the licence and bill-of-materials rule and how it is verified, the capability project check and
  coverage rules, the excluded GPU and model material, and the corrective actions. It MUST state that the toolchain is
  build-environment tooling, approves no library for product use, redistribution, or production, and sets no production
  value.

### Key Entities

- **Pinned library set**: The declared, hash-verified CPU-only wheels with exact versions, platform tags, publisher
  sources, the licences of the wheels and their bundled native libraries, the fixed allow-list of permissive SPDX
  identifiers, and the curated licence table.
- **Software bill of materials**: The recorded inventory of the added wheels and bundled libraries with digests and
  licences, produced and checked by the environment.
- **Verified capability project**: An existing project under `src/analysts/capabilities/` with its manifest, committed
  lock, package build, tests, and covered source scope.
- **Verification result**: The selected checks, outcomes, failed stage, covered and uncovered paths, and recommended
  action.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A fresh supported runner with no capability project completes setup and the synthetic smoke check using
  only the pinned toolchain and library set, with no download after provisioning.
- **SC-002**: A fixture capability project that locks the pinned libraries passes the locked sync, build, and test
  sequence, and a project that locks any other variant, version, or a GPU or TensorRT package fails with the package
  named.
- **SC-003**: Every injected download, digest, compatibility, source-distribution, disallowed-variant, smoke-check, and
  licence failure produces a failed result naming the responsible library or project, the stage, and one recommended
  action.
- **SC-004**: The licence check passes the pinned set and its bill of materials lists 100% of the added wheels and their
  bundled native libraries; each disallowed-licence, non-allow-listed, evidence-versus-table disagreement or gap, and
  statically linked or non-distinct LGPL fixture fails.
- **SC-005**: No path under `src/analysts/capabilities/` is reported as covered unless its project exists, and no
  project is verified more than once per invocation.
- **SC-006**: All existing environment-interface and check-selection regression cases pass, the diff of the change
  touches only the two action folders and this spec's `tasks.md`, and verification leaves committed declarations
  unchanged.

## Assumptions

### Dependencies

- [Analyst and Media Verification Environment](../archive/20261017-163801-analyst-media-environment/spec.md) (#143) is
  implemented and provides the pinned CPython, uv, FFmpeg and ffprobe, the locked Analyst project check, and the
  extension points this feature extends.
- [Reference Analyst](../archive/20261018-095952-reference-analyst/spec.md) (#170) is implemented and provides the first
  project under `src/analysts/capabilities/`, whose verification this feature must preserve.
- [Shot-Boundary Media Context Analyst](../20261023-022148-shot-boundary-analyst/spec.md) (#243) and every later
  learned-detector capability depend on this feature when OpenCV is not otherwise available.

### Architecture Alignment

- [Analyst Technology](../../docs/architecture/platform-implementation.md#analyst-technology) and
  [Analytics](../../docs/architecture/tenancy-and-technology.md#analytics) name the locked Python projects and the
  OpenCV, NumPy, and ONNX Runtime CPU/CUDA stack; this feature provisions only the CPU parts.
- [Low-Level Capabilities](../../docs/architecture/analyst-capability-catalog.md#low-level-capabilities) names CPU
  baselines on OpenCV and ONNX Runtime for the media-context and detection capabilities.
- [Model Governance](../../docs/architecture/analysts-models-and-hardware.md#model-governance) requires a recorded
  licence and provenance audit of every named framework and transitive dependency; the licence and bill-of-materials
  check implements that for the added wheels. Exact versions stay provisional until the capability evidence exists.

### Scope Boundaries

- The Linux x64 runner and the pinned CPython remain the only supported host and interpreter; no other platform or
  interpreter is added.
- Installing the libraries does not approve them for product images, redistribution, patent coverage, or production, and
  sets no production toolchain, library, or model value. Capability images keep their own pins and audits.
- Out of scope: any Analyst capability, SDK, model, corpus, or dataset; GPU, CUDA, TensorRT, and accelerator runtimes;
  model weights; the platform, Web UI, Manager, and documentation outside the two action folders; publication
  workflows; and new cache infrastructure.
- Successful environment checks are not evidence of Analyst quality or production readiness.
- Exact library versions, the standard bill-of-materials format, and how the pinned wheels are made available to the
  locked sync are planning decisions, constrained by FR-002, FR-003, FR-007, and FR-010.

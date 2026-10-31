# Quickstart: Validating the CV and Inference Environment

Prerequisites: Node.js 24, `python3` 3.12 (standard library only), `jq`, `curl`, `tar`, and `sha256sum` on Linux x64.
The environment-local tests use fixtures and stubbed commands and need no network, Docker, or product project.

## 1. Run the environment-local tests

```bash
node --test .github/actions/environment-setup/*.test.mjs .github/actions/environment-verify/*.test.mjs
```

Expected: all existing cases pass unchanged, and the new cases for the pin declaration, digest, compatibility,
version, source-distribution, and disallowed-variant failures, the licence and SBOM kinds listed in the
[contract](contracts/environment-interface.md), the smoke check passing and failing, the tools-disabled mode, and the
capability project selection and coverage fixtures (none, one, several, deleted; task, phase, finalize) pass.

## 2. Verify the pins on a fresh supported runner (SC-001, SC-002)

Run `environment-setup` with `install-tools: true` from a workflow or locally with `RUNNER_TEMP` set. Expected output
ends with `Verified Analyst CV libraries: ...` and `Verified synthetic CV smoke check: ...`, the install root holds
`notices/analyst-cv/sbom.cdx.json`, and nothing is downloaded after the wheelhouse is complete.

## 3. Verify a capability project

Run `environment-verify` in `finalize` mode against a checkout containing
`src/analysts/capabilities/reference-media-probe`. Expected: the `library pin` stage reports no finding for a project
that locks none of the libraries, the locked sync, wheel build, and pytest stages pass, and the covered paths include
the project's root only while it exists.

## 4. Confirm confinement (SC-006)

```bash
git diff --name-only origin/main...HEAD
```

Expected: only paths under the two action folders and this spec's `tasks.md`; committed declarations unchanged by a
verification run. Run `node .github/scripts/check-markdown.mjs --files .github/actions/environment-setup/README.md
.github/actions/environment-verify/README.md` after the documentation task.

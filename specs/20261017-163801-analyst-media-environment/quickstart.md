# Validation Guide: Analyst and Media Verification Environment

This is the implementation's validation guide. The new commands and files below are planned artifacts and do not exist merely because this plan was authored.

## Prerequisites

- The existing Linux x64 automated verification runner and caller-provided Node.js.
- Existing platform, Manager, Web UI, and container prerequisites remain unchanged.
- The implementation contains the two action folders' new helpers and tests.
- Use disposable fixture workspaces and synthetic media only. Do not target developer or production data.

## Environment-Local Tests

Run the following from the repository root on the supported Linux runner:

```text
node --test .github/actions/environment-setup/toolchain.test.mjs .github/actions/environment-verify/analyst-projects.test.mjs
```

The tests must exercise the actual installation/selection/execution helpers with isolated fixture workspaces and stub external tools. Testing only a duplicate model of the action logic is insufficient.

Required matrix:

| Area | Cases and expected result |
| --- | --- |
| Host and tools | Correct pins pass; unsupported host, failed download, wrong digest, unsafe archive, unexpected layout, wrong executable version, failed probe, and failed decode fail with guidance |
| Tools-disabled mode | No new executable download or media smoke check; established cache behavior preserved |
| Absent products | New product checks skipped; no Python coverage added; existing checks retain their behavior |
| Product discovery | SDK and multiple capabilities recognized; generated/dependency directories excluded; malformed or unsupported manifests fail explicitly |
| Ownership | Deleted or missing manifest leaves source uncovered; similarly prefixed siblings do not match; Manager ownership unchanged |
| Dependencies | SDK changes select consumers once; transitive consumers selected; missing dependency, path escape, duplicate identity, and cycle fail |
| Modes and triggers | Task/phase select affected projects; finalize selects all; analysis/common contracts and environment changes select all; unrelated changes select none |
| Locked execution | Both sync stages and exact command order; missing/stale lock, unavailable external wheel, missing local build backend, and incompatible Python fail; no declaration mutation, runtime download, or isolated build resolver |
| Package and tests | Build or backend failure, failing test, missing pytest, and no tests fail; passing package and nonempty passing tests succeed |
| Reporting | Every injected failure names its tool/project, stage, and one action; failed status and existing outputs survive; no false merge-readiness claim |
| Regression | Existing action inputs/outputs, check selection, sequential platform tests, cache boundaries, and fail propagation preserved |

Do not add product fixtures to `src/` or root contracts for these tests. Define the fixtures within the permitted action folders and materialize them only in isolated temporary directories.

## Real Toolchain Evidence

Run the modified setup action with `install-tools: true` in the existing automated environment. Verify:

1. Each exact archive matches the committed SHA-256 before extraction.
2. CPython, uv, FFmpeg, and ffprobe report their planned versions/build revisions.
3. The native synthetic fixture can be created, extracted, probed, and decoded without downloading another executable.
4. Notices and provenance are retained beside the installed tools.
5. Temporary fixture cleanup and tools-disabled mode work as specified.

Then verify an isolated minimal Python package through the real locked sync, no-isolation wheel build, and no-sync pytest commands in the [interface contract](contracts/environment-interface.md). Include the locked build backend and a nonempty test suite in this fixture. Generate its fixture declarations outside product source; do not commit a placeholder SDK.

Repeat with a stale lockfile and a failing test. Both must fail with the named stage and a concrete recommended action, without changing committed declarations.

## Repository And Merge Gates

- Run the existing environment-feature self-verification in its read-only verification job; do not change workflows to bypass or extend its permissions.
- Exercise the no-product state now and the project-present state with isolated fixtures. Later product features provide their own executable evidence.
- Keep existing checks enabled and verify all caller-facing outputs.
- Validate documentation from the repository root:

```text
node .github/scripts/check-markdown.mjs
```

This feature requires a person's review before merge because it changes the environment used to verify later implementations. A successful smoke check is not evidence of Segment Service correctness, model quality, deployment support, or production readiness.

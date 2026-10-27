# Implementation Plan: Docker Push Digest from the Status Line

**Branch**: `20261027-082724-docker-push-digest-status-line` | **Date**: 2026-10-27 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/archive/20261027-082724-docker-push-digest-status-line/spec.md`

## Summary

`DockerEngine.PushAsync` in the End-to-End test support reads the pushed manifest digest only from an `aux.Digest` entry of the engine's JSON progress stream. The Docker engine of the GitHub Actions runner reports it only in a status line `<tag>: digest: sha256:<64 hex> size: <n>`. The change moves the progress-stream parsing into a pure internal helper, `PushDigestReader`, that keeps the `aux` digest authoritative, falls back to a strictly anchored status-line match, keeps failing on `errorDetail`, and throws with the engine output when no well-formed digest is reported. `PushAsync` calls the helper; deterministic unit-level tests cover the six cases without Docker.

## Technical Context

**Language/Version**: C# on .NET 10 (`src/platform/global.json`).

**Primary Dependencies**: `System.Text.Json` and `System.Text.RegularExpressions` from the shared framework, xUnit v3, Shouldly (existing). No new dependency.

**Storage**: N/A.

**Testing**: `SocAlytics.Platform.EndToEnd.Tests`: new Docker-free parsing tests, plus the Docker-backed `ReferenceAnalystImage` and `ShotBoundaryImage` tests on a Linux engine.

**Target Platform**: Linux (Docker engine socket `/var/run/docker.sock`); parsing tests run anywhere.

**Project Type**: Test-support change in the platform monolith's End-to-End test project.

**Performance Goals**: The six parsing tests finish in under 5 seconds in total (SC-001).

**Constraints**: Only files under `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`; no production code, sleeps, retries, or timeout changes; `aux` digest wins; digest accepted by form, not by tag.

**Scale/Scope**: One helper method, one call site, one test class.

## Constitution Check

| Principle | Check | Result |
| --- | --- | --- |
| I. Architecture is the design authority | No design decision changes; see Architecture Updates. | Pass |
| II. Source-area ownership | Only `src/platform/Tests`; no new `src` child. | Pass |
| III. API-first control plane | Test support talks to the Docker Engine API as before; no platform behavior changes. | Pass |
| IV. Evidence over claims | Parsing tests plus probes that must fail them, and the Docker-backed image tests on a Linux engine; results are development evidence only. | Pass |
| V. Focused, minimal changes | One extracted helper and its tests; no package or abstraction beyond that. | Pass |
| Environment features | Needs only the .NET SDK and, for the image tests, Docker already provided by `environment-setup`. No environment spec. | Pass |

Post-design re-check: Pass.

## Architecture Updates

None. The change only widens how End-to-End test support reads the digest of a Docker push, in files under `src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/`. No narrative, design decision, or implementation-status statement in `docs/architecture/` names this helper or changes (a search for `aux.Digest`, `TestAnalystImage`, `DockerEngine`, and push-digest wording found none), so no documentation task for `docs/architecture/` is required.

## Design

1. **Extract the parser (FR-006).** Add `internal static class PushDigestReader` with `string Read(string body)` to `Support/TestAnalystImage.cs` (next to `DockerEngine`, which stays its only caller). `PushAsync` keeps the HTTP call and returns `PushDigestReader.Read(body)`.
2. **Parse each non-empty line as JSON (FR-001, FR-004).** An `errorDetail` property throws `InvalidOperationException("Pushing the Test Analyst image failed: " + error.GetRawText())` immediately, wherever it appears. An `aux.Digest` string sets the `aux` digest (last wins). The throwing behavior is pinned: a line that is not JSON throws `JsonException`, and a JSON line that is not an object, an `aux` that is not an object, or an `aux.Digest` that is not a string (including JSON null) throws `InvalidOperationException`; a null never overwrites an earlier `aux` digest.
3. **Status-line fallback (FR-002, FR-003).** A `status` property that is a string and matches the anchored pattern `^[A-Za-z0-9_][A-Za-z0-9_.-]*: digest: (sha256:[0-9a-f]{64}) size: [0-9]+\z` sets the status digest (last wins); an absent or non-string `status` is ignored, the tag is not compared with the pushed one, and upper-case hexadecimal, other algorithms, other lengths, and surrounding text (including a trailing newline or space after the size) do not match. The result is the `aux` digest when present, otherwise the status digest.
4. **Failure (FR-005).** Neither source: `InvalidOperationException("The push did not report a manifest digest:\n" + body)`, so a malformed candidate fails the same way.
5. **Tests (FR-006, SC-001).** `PushDigestReaderTests` in the End-to-End test project covers aux only, status line only, both (aux wins), neither (message contains the output), error entry before and after a digest, malformed digests (63 and 65 characters, upper case, `sha512:`, extra text, a trailing newline or space after the size, non-hex), unrelated progress lines, a non-string `status`, a non-JSON line, a non-object JSON line, a non-object `aux`, a non-string or null `aux.Digest` (a null after a valid `aux` digest still throws), an `errorDetail` message containing the error text, a different tag, and last-match-wins. Each asserts on the returned value or the exception, so a broken behavior fails the test.
6. **Prove it (FR-008, SC-002, SC-003).** Run the parsing tests, then `ReferenceAnalystImage` and `ShotBoundaryImage` on a Linux engine; with two temporary probes (ignoring the status line; accepting a 63-character digest) at least one parsing test fails on its assertions each; remove the probes. SC-005 is post-merge evidence.

## Project Structure

### Documentation (this feature)

```text
specs/archive/20261027-082724-docker-push-digest-status-line/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── spec.md
└── checklists/
    └── requirements.md
```

`contracts/` is not created: the feature adds no external interface.

### Source Code (repository root)

```text
src/platform/Tests/SocAlytics.Platform.EndToEnd.Tests/
├── Support/TestAnalystImage.cs          # PushDigestReader extracted; PushAsync calls it
└── PushDigestReaderTests.cs             # new, Docker-free
```

**Structure Decision**: Keep the helper beside its only caller in the existing support file and add one test class at the project root like the other test classes; no new project, folder, or package.

## Complexity Tracking

No constitution violations to justify.

# Research: Docker Push Digest from the Status Line

No `NEEDS CLARIFICATION` remains; the clarifications of the spec fix the precedence, scope, and no-retry rules.

## Decision 1: Accept the digest from the status line as a fallback

- **Decision**: Keep `aux.Digest` authoritative and use the `status` line `<tag>: digest: sha256:<64 hex> size: <n>` only when no `aux` digest was reported.
- **Rationale**: Both name the same manifest for a successful push; the runner's engine emits only the status line, and the existing engines emit `aux`. Precedence keeps behavior on existing engines unchanged.
- **Alternatives considered**: Status line only (breaks existing engines); query the registry or `GET /images/{name}/json` for `RepoDigests` after the push (an extra call that depends on the registry's response and widens the change); retry or sleep until an `aux` entry appears (the engine never emits one, and the spec forbids retries and sleeps).

## Decision 2: Anchored pattern, accepted by form

- **Decision**: A compiled `Regex` anchored at both ends: `^[A-Za-z0-9_][A-Za-z0-9_.-]*: digest: (sha256:[0-9a-f]{64}) size: [0-9]+\z`. The end anchor is `\z` because `$` would also accept a trailing newline.
- **Rationale**: Only a well-formed status-line digest is accepted; the aux digest is taken as the engine reports it, unchanged from today. The engine reports lower-case digests, and a differing tag is still a valid report. The tag shape is the Docker tag grammar without its length cap, which adds nothing here.
- **Alternatives considered**: Substring search for `sha256:` (accepts malformed or surrounding text); comparing the tag with the pushed tag (the spec accepts by form); case-insensitive matching (the spec excludes upper case).

## Decision 3: Pure parser separate from the engine call

- **Decision**: An internal static `PushDigestReader.Read(string body)` in `Support/TestAnalystImage.cs`, tested from a new Docker-free test class.
- **Rationale**: Satisfies FR-006 without a socket or registry; `InternalsVisibleTo` is not needed because the tests are in the same assembly.
- **Alternatives considered**: A new file (the helper is tiny and has one caller); an interface over `DockerEngine` with a fake (more abstraction than the parsing needs).

## Decision 4: Error handling stays as is

- **Decision**: `errorDetail` fails immediately wherever it appears; a line that is not JSON keeps throwing from `JsonDocument.Parse`; a missing or malformed digest throws `InvalidOperationException` with the full engine output.
- **Rationale**: No behavior change beyond widening what counts as the digest report (FR-004, FR-005, FR-007).
- **Alternatives considered**: Tolerating non-JSON lines (outside the scope).

## Decision 5: Verification and probes

- **Decision**: Parsing tests run without Docker; `ReferenceAnalystImage` and `ShotBoundaryImage` verify on a Linux engine; two temporary probes (ignore the status line; accept 63 characters) show the tests detect regressions and are removed afterward.
- **Rationale**: SC-001 to SC-003 and Principle IV.
- **Alternatives considered**: Docker-backed tests alone (slow, engine-dependent, cannot cover malformed digests).

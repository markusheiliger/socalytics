# Research: Read the Pushed Manifest Digest from Every Engine's Push Output

No `NEEDS CLARIFICATION` remains in the plan's Technical Context.

## Decision 1: Parse the whole push body in a pure function

- **Decision**: Move the line loop of `DockerEngine.PushAsync` into `PushOutput.ReadDigest(string body)` (internal, static, in `Support/TestAnalystImage.cs`); `PushAsync` reads the response body and returns its result.
- **Rationale**: The parsing can then be tested with recorded streams without Docker (SC-002), and `PushAsync` keeps its signature (FR-007).
- **Alternatives considered**: Testing only through the Docker-backed tests (rejected: it cannot exercise both push-output styles on one engine); a new file for the parser (rejected: FR-006 names `TestAnalystImage.cs`).

## Decision 2: Status line recognition

- **Decision**: Accept a `status` string only when it matches the anchored expression `^\S+: digest: (sha256:[0-9a-f]{64}) size: \d+$`, and return capture group 1.
- **Rationale**: Anchoring on the `digest:` and `size:` words excludes layer progress and "Layer already exists" lines (edge case 1); the fixed 64 lowercase hex characters exclude shorter, longer, and uppercase values (edge case 2); the capture keeps the `sha256:<hex>` form (FR-003).
- **Alternatives considered**: An unanchored `sha256:[0-9a-f]{64}` search of the whole body (rejected: it matches layer lines and values inside other properties); `\w+` for the tag (rejected: tags may contain `.` and `-`).

## Decision 3: Precedence and failure order

- **Decision**: Read every line. An `errorDetail` anywhere throws the existing `Pushing the Test Analyst image failed: <raw>` error, even after a digest line. Otherwise the `aux.Digest` value wins over a status-line digest; if neither exists, throw `The push did not report a manifest digest:` plus the body.
- **Rationale**: Matches acceptance scenarios 3 to 5 and FR-001, FR-004, and FR-005; the `aux` path stays authoritative as before.
- **Alternatives considered**: Throwing when the two digests disagree (rejected: the spec says `aux` wins and requires no new failure mode).

## Decision 4: Tolerate unparseable lines

- **Decision**: Skip a line that is not valid JSON or not a JSON object, instead of letting `JsonDocument.Parse` throw; only a `string` `status` and a `string` `aux.Digest` are used.
- **Rationale**: A stray line must neither hide a digest on a later line nor turn the helper's descriptive error into a `JsonException` (edge case 3).
- **Alternatives considered**: Keeping the throw (rejected: violates the edge case).

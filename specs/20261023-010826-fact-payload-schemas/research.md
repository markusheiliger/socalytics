# Research: Capability Fact Payload Schemas

All Technical Context unknowns are resolved here. Limits are development values; production values stay unresolved.

## R1: Where the declaration lives and how it is pinned

- **Decision**: The schemas are declared in the Analyst manifest under `output.factPayloadSchemas` (fact kind → schema),
  released as manifest contract `1.2.0`. They are stored with the cached manifest in
  `analyst_capability_declaration.manifest` (already a `jsonb` copy of the whole manifest, shown by the registered-image
  read), so no migration is needed. Profile and workflow versions bind the image digest, which addresses the immutable
  declaration, so they pin the schemas transitively. Each run's node snapshot pins them explicitly as
  `factPayloadSchemas` plus `factPayloadSchemasDigest`, and omits both keys when the declaration has none, so snapshots
  and run digests of existing runs and seeds are unchanged.
- **Rationale**: Reuses the existing content-addressed chain (image digest → declaration → profile → node snapshot),
  adds no table, and keeps acceptance independent of later registrations because it reads only the node snapshot.
- **Alternatives**: A separate schema table keyed by digest (new migration, no benefit); resolving schemas from the
  declaration at acceptance (would break pinning if a declaration were ever re-imported).

## R2: Schema language subset and limits

- **Decision**: A restricted subset of JSON Schema 2020-12, enforced twice: structurally by a `payloadSchema`
  definition in the manifest schema (unknown keywords refused through `additionalProperties: false`) and by the
  import-time limit checks of the platform and the SDK. Allowed keywords: `type` (`object`, `array`, `string`,
  `number`, `integer`, `boolean`, `null`; one name), `properties`, `required`, `additionalProperties` (boolean or
  schema), `items` (one schema), `enum`, `const`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`,
  `minLength`, `maxLength`, `minItems`, `maxItems`, `$defs`, `$ref` (only `#/$defs/<name>`), and the annotations
  `title` and `description`. Excluded: `pattern` and `format` (no regular-expression engine differences, no ReDoS, no
  format assertions), `multipleOf`, `uniqueItems`, `prefixItems`, `oneOf`/`anyOf`/`allOf`/`not`/`if`, `$id`,
  `$schema`, `$anchor`, `$dynamicRef`, every external `$ref`.
- **Limits (development values)**: at most 64 kinds per manifest, 16 KiB per schema, schema depth 8, 256 schema
  nodes per schema, `$ref` targets acyclic (a cycle makes the manifest invalid, so evaluation always terminates),
  instance depth 32.
- **Rationale**: Every keyword has the same meaning in `JsonSchema.Net` (platform) and `jsonschema` (SDK); the
  excluded keywords are exactly where implementations diverge. Shared golden payload cases (R6) prove identical
  verdicts. Acyclic `$ref` plus the node and depth limits make evaluation cost proportional to the payload.
- **Alternatives**: Full 2020-12 (divergent verdicts, unbounded evaluation); a custom schema dialect (violates
  Principle III, JSON Schema 2020-12 is the adopted contract language).

## R3: Platform validator placement

- **Decision**: A new Application port `IFactPayloadValidator` (compile a node's pinned schemas once per
  verification, validate each payload, return the first violation with kind, fact index, and a JSON pointer) with a
  `JsonSchema.Net` implementation in Infrastructure (`Registry/FactPayloadValidator.cs`), formats not asserted.
  `ResultManifestVerifier` calls it after the existing envelope, size, count, and window checks, so existing reasons
  keep precedence. The manifest import in `AnalystManifestValidator` calls the same subset checker
  (`FactPayloadSchemaLimits`, Domain) and refuses with a named reason.
- **Bound**: validation stops at `Analysis:Results:PayloadValidation:MaxDuration` (default 5 s, 1 s to 60 s) and ends
  the attempt with `result-payload-validation-bound`. Memory is bounded by validating only payloads already inside
  the existing per-result facts size limit, with instance depth 32 and no retained copies; an instance deeper than 32 ends the attempt with `result-payload-validation-bound`; options are validated like `Analysis:Results` today.
- **Atomicity**: Verification already runs before the accepting unit of work, and facts are indexed in the acceptance
  transaction (FR-009), so a rejection leaves no record. The second pass of `CompleteAttempt` re-checks under locks and
  is unchanged.
- **Alternatives**: Validating inside the unit of work (holds locks during CPU work); a hand-written validator (two
  implementations of the same subset to keep in step).

## R4: Named reasons and bounded detail

- **Decision**: Reason codes `result-payload-invalid`, `result-payload-kind-undeclared`, and
  `result-payload-validation-bound`, added to `ResultRejectionReasons`. The stored reason carries a bounded locator,
  for example `result-payload-invalid[kind=frame-second-probe;fact=3;count=2;at=/decodedFrames]` (at most 256
  characters, never a payload value). Metrics and audit records use the code as the reason tag, so payload failures
  are distinguishable (FR-018). The implementation must confirm that the completion-outcome reason column and the
  `attempt-completion` response contract accept the bounded form, and widen only what blocks it.
- **Alternatives**: A new structured field in the completion contract (contract change with no consumer).

## R5: SDK behavior

- **Decision**: The SDK reads its own manifest at `SOCALYTICS_ANALYST_MANIFEST` (default
  `/opt/socalytics/analyst-manifest.json`, where Analyst images carry it). No file means no declared schemas and
  unchanged behavior; an unreadable or invalid file is an input error. `ResultWriter.add_fact` validates the payload
  against the schema of its kind with `jsonschema` (Draft 2020-12, no format checker) before staging the fact, and
  raises `ResultError` with the new categories `fact-payload-invalid` and `fact-kind-undeclared`; there is no
  warning mode. The writer also accepts an explicit manifest object so unit tests need no container.
- **Rationale**: Honors the clarification (automatic, always fatal), needs no opt-in call, and uses the library the
  SDK already depends on.

## R6: Shared golden examples and release

- **Decision**: Manifest contract `1.2.0`: `manifestVersion` enum gains `1.2.0`, `releases/1.2.0.schema.json` is the
  released baseline, `1.0.0` and `1.1.0` baselines stay byte-identical. New valid and invalid manifest examples (with
  and without schemas; each refusal of FR-002: invalid schema, unsupported keyword, external `$ref`, cyclic `$ref`,
  size, depth, kind count) extend `expectations.json`: the first error pointer only for schema-expressible refusals
  (unknown keyword, external `$ref`, schemas on an old manifest version) and a named Domain refusal code for the
  code-checked ones (cyclic `$ref`, size, depth, root type, kind count), asserted by running `FactPayloadSchemaLimits`. A new `examples/fact-payloads.json` lists cases
  (`manifest` example, `kind`, `payload`, `valid`, expected JSON pointer) used by both the platform Contracts tests and
  the SDK tests; the SDK copy comes from `scripts/sync_contracts.py`, whose `ANALYSIS_CONTRACTS` list gains `analyst-manifest` (the
  copy did not carry that contract before).

## R7: Reference capability 1.1.0 and end-to-end evidence

- **Decision**: `reference-media-probe` 1.0.0 files stay untouched. 1.1.0 adds `analyst-manifest.payload-validated.json`
  (manifest `1.2.0`, schema from `schemas/frame-second-probe.payload.schema.json` reduced to the subset) and
  `Dockerfile.payload-validated` (same code, its own pinned manifest label), built by `ReferenceAnalystImage` as a
  second image. Accepted path: run it end to end and compare read-back payloads with the written ones. Rejected path
  without a production hook: a derived test image (through `TestAnalystImage`) whose registered manifest declares a
  stricter schema (for example `decodedFrames` maximum 0) while the in-container SDK manifest declares none, so the
  Analyst legitimately writes facts that the platform then rejects with `result-payload-invalid` and exposes no fact.
- **Alternatives**: A fault-injection switch in the reference Analyst (test-only behavior in shipped code).

## R8: Environment

- **Decision**: No new tooling. .NET 10, `JsonSchema.Net 8.0.5`, Python 3.12 with `jsonschema>=4,<5`, `uv`, and Docker
  (Linux engine) for end-to-end evidence are already provided by `.github/actions/environment-setup` and
  `environment-verify`.

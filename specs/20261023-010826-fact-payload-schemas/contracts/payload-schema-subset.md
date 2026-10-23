# Contract Delta: Analyst Manifest 1.2.0 and the Payload Schema Subset

The executable contract is `contracts/analysis/analyst-manifest/v1/` (released baseline
`releases/1.2.0.schema.json`); this page states what the implementation adds.

## Manifest change (minor, backward compatible)

- `manifestVersion` enum: `["1.0.0", "1.1.0", "1.2.0"]`; `x-socalytics-version` becomes `1.2.0`.
- `output.factPayloadSchemas` (optional): object, at most 64 properties, property name = fact kind, value =
  `#/$defs/payloadSchema`. A manifest declaring it MUST use `manifestVersion` `1.2.0`; `1.0.0` and `1.1.0` manifests
  stay valid and declare none.
- `$defs/payloadSchema`: the closed keyword set below, `additionalProperties: false`.

| Group | Keywords |
| --- | --- |
| Type | `type` (one of `object`, `array`, `string`, `number`, `integer`, `boolean`, `null`), `enum`, `const` |
| Object | `properties`, `required`, `additionalProperties` (boolean or schema) |
| Array | `items` (one schema), `minItems`, `maxItems` |
| Number | `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum` |
| String | `minLength`, `maxLength` |
| Reuse | `$defs`, `$ref` of the form `#/$defs/<name>` only |
| Annotations | `title`, `description` |

Refusals that code checks (the JSON Schema cannot express them): `$ref` that does not resolve or forms a cycle,
more than 16 KiB, depth above 8, more than 256 schema nodes, root `type` other than `object`, more than 64 kinds. Their golden examples record a named
Domain refusal code in `expectations.json` instead of a JSON Schema error pointer.

## Acceptance semantics

- Validated: the `payload` of every fact of every fact artifact of a result, against `factPayloadSchemas[kind]` of the
  run's node snapshot, using JSON Schema 2020-12 semantics without format assertion, on the submitted JSON values.
- A fact whose kind has no entry, while the snapshot has any entry, fails with `result-payload-kind-undeclared`.
- Reasons: `result-payload-invalid`, `result-payload-kind-undeclared`, `result-payload-validation-bound`, each with
  the bounded locator of `data-model.md`. Payloads are never rewritten.

## Golden examples (shared)

- `examples/valid/`: `segment-with-payload-schemas.json`, `match-with-payload-schemas.json`; the existing four stay.
- `examples/invalid/` (all listed in `expectations.json`: the first error pointer for schema-expressible refusals, a
  named Domain refusal code for code-checked ones): `payload-schema-unknown-keyword`,
  `payload-schema-external-ref`, `payload-schema-cyclic-ref`, `payload-schema-too-deep`, `payload-schema-too-large`,
  `payload-schema-root-not-object`, `payload-schemas-too-many-kinds` (65 kinds), `payload-schemas-on-old-version`.
- `examples/fact-payloads.json`: cases `{ manifest, kind, payload, valid, at }` over the valid examples, read by the
  platform Contracts tests and the SDK tests (SDK copy refreshed by `scripts/sync_contracts.py`).

## SDK surface

- Error categories `fact-payload-invalid` and `fact-kind-undeclared` (exit code 1); messages name the kind and the
  fact position, never payload values.
- Manifest location: `SOCALYTICS_ANALYST_MANIFEST`, default `/opt/socalytics/analyst-manifest.json`.

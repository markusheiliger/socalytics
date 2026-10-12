# Contracts

This folder holds the canonical machine-readable contracts of SocAlytics. They are JSON Schema 2020-12 documents and are authoritative over generated code.

## Conventions

- Each schema lives at `contracts/<area>/<name>/v<major>/<name>.schema.json`, except the shared definitions, whose folder `contracts/common/v<major>/` is named in [contracts and compatibility](../docs/architecture/contracts-and-compatibility.md): one schema per major version at `contracts/common/v<major>/common.schema.json` (area `common`, no name segment).
- The `$id` is `https://socalytics.invalid/` followed by the schema's repository path, for example `https://socalytics.invalid/contracts/<area>/<name>/v<major>/<name>.schema.json`. The `.invalid` domain is reserved and never dereferenced; every `$ref` resolves offline to another schema in this folder.
- The exact version is recorded in `x-socalytics-version` (and in each payload's `contractVersion` where the payload has one).
- Every schema has exactly one index row whose Artifact cell begins with a Markdown link to the schema path relative to `contracts/`. Further links in the same cell, for example fixtures or released copies, are allowed.
- Immutable released copies live under a `releases/` folder, reuse their schema's `$id`, and are not separate artifacts.
- Digests use one of two forms and are never mixed: `sha-256:<64 lowercase hex>` and `sha-256-parts:<partSizeBytes>:<partCount>:<64 lowercase hex>`. A `sha-256-parts` value is never compared with a plain `sha-256` value.

## Index

| Artifact | Owner | Version | Example | Validation command |
| --- | --- | --- | --- | --- |
| [recordings/recordings-finalized/v1/recordings-finalized.schema.json](recordings/recordings-finalized/v1/recordings-finalized.schema.json) | Recordings (control plane) | `1.0.0` | embedded `examples` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` |
| [analyst-manager/registration/v1/registration.schema.json](analyst-manager/registration/v1/registration.schema.json) | Registry (control plane) and Analyst Manager | `1.0.0` | the manifest `fixtures.json` and the fixture files it lists, plus the schema's embedded `examples` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` and `dotnet test src/analysts/manager/SocAlytics.Analysts.Manager.slnx` |
| [common/v1/common.schema.json](common/v1/common.schema.json) | Future contract pipeline | `1.0.0` | exercised through the analysis examples; released baseline `common/v1/releases/1.0.0.schema.json` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` |
| [analysis/analyst-job/v1/analyst-job.schema.json](analysis/analyst-job/v1/analyst-job.schema.json) | Scheduler and Job Registry; Analyst capability owners for payload semantics | `1.0.0` | `examples/valid/` and `examples/invalid/` in the schema directory; released baseline `analysis/analyst-job/v1/releases/1.0.0.schema.json` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` |
| [analysis/attempt-completion/v1/attempt-completion.schema.json](analysis/attempt-completion/v1/attempt-completion.schema.json) | Scheduler and Job Registry | `1.0.0` | `examples/valid/` and `examples/invalid/` in the schema directory; released baseline `analysis/attempt-completion/v1/releases/1.0.0.schema.json` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` |
| [analysis/run-state-changed/v1/run-state-changed.schema.json](analysis/run-state-changed/v1/run-state-changed.schema.json) | Scheduler and Job Registry | `1.0.0` | `examples/valid/` and `examples/invalid/` in the schema directory; released baseline `analysis/run-state-changed/v1/releases/1.0.0.schema.json` | `dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests` |

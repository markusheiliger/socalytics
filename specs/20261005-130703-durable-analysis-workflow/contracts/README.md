# Contracts: Durable Analysis Workflow

These are the Phase 1 interface contracts of [plan.md](../plan.md). The
`schemas/` tree mirrors the repository-root `contracts/` directory (the
canonical location defined by
[Contracts and Compatibility](../../../docs/architecture/contracts-and-compatibility.md)),
so the relative `$ref` values resolve identically in both places. Recording
Lineage and Upload creates that directory, its index `contracts/README.md`, the
`$id` convention, and the contract test project
`src/platform/Tests/SocAlytics.Platform.Contracts.Tests`; this feature extends
them with the files below, index entries, and compatibility checks.

## Artifacts

| Plan file | Canonical repository path | Format | Semantic owner | Exact version |
| --- | --- | --- | --- | --- |
| [openapi.yaml](openapi.yaml) | Merged into the built-in `/openapi/v1.json` (no file copy) | OpenAPI 3.1 | Control plane/API | 1.0.0 |
| [common.schema.json](schemas/common/v1/common.schema.json) | `contracts/common/v1/common.schema.json` (new) | JSON Schema 2020-12 | Future contract pipeline | 1.0.0 |
| [analyst-job.schema.json](schemas/analysis/analyst-job/v1/analyst-job.schema.json) | `contracts/analysis/analyst-job/v1/analyst-job.schema.json` (new) | JSON Schema 2020-12 | Scheduler and Job Registry; Analyst capability owners for payload semantics | 1.0.0 |
| [attempt-completion.schema.json](schemas/analysis/attempt-completion/v1/attempt-completion.schema.json) | `contracts/analysis/attempt-completion/v1/attempt-completion.schema.json` (new) | JSON Schema 2020-12 | Scheduler and Job Registry | 1.0.0 |
| [run-state-changed.schema.json](schemas/analysis/run-state-changed/v1/run-state-changed.schema.json) | `contracts/analysis/run-state-changed/v1/run-state-changed.schema.json` (new) | JSON Schema 2020-12 | Scheduler and Job Registry | 1.0.0 |
| Not copied here (consumed only) | `contracts/recordings/recordings-finalized/v1/recordings-finalized.schema.json` (existing, created by Recording Lineage and Upload) | JSON Schema 2020-12 | Recordings | 1.0.0 |

The consumer reads the event identity, contract version, occurrence time,
recording-set version, Match, Team, and the ordered members (recording-version
and timeline-mapping identities and digests) as that schema defines them, and
confirms all of them through `IRecordingSetLookup` before creating a run.

## Published and consumed messages

| Message type | Direction | Subject (development default) | Payload contract | Stable identity |
| --- | --- | --- | --- | --- |
| `matches.recordings-finalized` | Published (relayed from `recording_finalized_events`) and consumed | `matches.recordings-finalized` | Recordings-owned recordings-finalized event v1 | `eventId` (also the outbox message id) |
| `analysis.ready-work` | Published | `analysis.jobs.ready.<capabilityId>` | analyst-job v1 | `messageId` (one per readiness occurrence) |
| `analysis.run-state-changed` | Published | `analysis.events.run-state-changed` | run-state-changed v1 | `messageId` |

Subjects, stream names, and consumer names are development defaults bound
through configuration; production values are deferred by the spec. Every
published message uses its `messageId` as the JetStream `Nats-Msg-Id` header
(suffixed with `.r<round>` for reconciliation rounds). Consumers de-duplicate by
the payload identity and revalidate against durable state; a message confers no
authority.

## Conventions applied

- `$id` values follow the convention established by Recording Lineage and
  Upload: the reserved, never-resolvable base
  `https://socalytics.invalid/contracts/` followed by the repository-relative
  path; every `$ref` is relative and resolves offline against the referencing
  schema's `$id`, through the single `ContractCatalog.LoadRegistry()` of the
  contract test project (and the embedded registry of the runtime validator).
- The exact version is recorded by the `x-socalytics-version` annotation and by
  each payload's `contractVersion` (`1.x.y` accepted by the v1 family).
- Every object is closed (`additionalProperties: false`). This is what rejects
  Manager, host, runtime-product, accelerator-model, preprocessing, stamp, and
  club fields in jobs, and credentials, URLs, connection details, and media in
  every payload. Object references use the `objectKey` pattern, which cannot
  express a scheme, query string, or signature parameter.
- Identifiers are opaque strings, timestamps are RFC 3339 UTC, durations are
  integer seconds, fencing tokens are positive integers no larger than
  `9007199254740991`, digests are `sha-256:<64 lowercase hex>`.
- Completion payloads contain no floating-point numbers so that the RFC 8785
  canonical form used for idempotent replay comparison is unambiguous.

## Examples and compatibility corpus (created at implementation)

Each new analysis schema directory receives the example files below, and every
new schema directory, including `contracts/common/v1/`, receives a released
copy (the shared definitions are exercised through the analysis examples):

- `examples/valid/*.json`: at least a segment-scoped job, a match-scoped job
  with segment-barrier lineage, a succeeded completion, a failed completion, and
  a run-state-changed event;
- `examples/invalid/*.json`: one file per rejection rule, including a job naming
  a Manager, host, runtime product, accelerator model, or tile; completions
  missing attempt identity, attempt number, fencing token, idempotency key, or
  lineage; payloads carrying a credential field, a presigned URL, a broker
  connection string, or inline media; a succeeded completion without a result;
- `releases/1.0.0.schema.json`: the immutable released copy used as the
  same-major compatibility baseline.

Proposed breaking revisions (removed or renamed property, newly required
property, narrowed enum, narrowed pattern, newly closed object) live as fixtures
in the existing `src/platform/Tests/SocAlytics.Platform.Contracts.Tests` and
must fail the compatibility check this feature adds there. The existing index
`contracts/README.md` gains one appended row per new schema (artifact, owner,
exact version, example, validation command); existing rows are never rewritten.
The merged `ContractCatalog` keeps `releases/` copies out of discovery, the
index check, and `LoadRegistry()`, and exposes them through
`ContractCatalog.Releases` for the compatibility check.

## Validation command

The existing contract command from Recording Lineage and Upload:

```powershell
dotnet build src/platform/SocAlytics.Platform.slnx --no-restore
dotnet test src/platform/Tests/SocAlytics.Platform.Contracts.Tests --no-build
```

The same tests also run as part of the documented
`dotnet test src/platform/SocAlytics.Platform.slnx --no-build`. They are offline
and deterministic: the schema registry is built only from files under
`contracts/`, and every outcome is asserted twice in one run.

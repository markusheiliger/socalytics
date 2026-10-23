# Contracts: Match Conversation Archive and Delete

| Contract | Kind | Where it is implemented and validated |
| --- | --- | --- |
| [lifecycle-operations.md](lifecycle-operations.md) | Member HTTP operations (`/api/v1`) | OpenAPI document `/openapi/v1.json` and its committed snapshot (`OpenApiSnapshotTests`); Integration tests |
| [purge-event.md](purge-event.md) | Outbox message schema | New `contracts/agent-orchestration/purge-event/v1/purge-event.schema.json` with `examples/valid/`, `examples/invalid/`, and `releases/1.0.0.schema.json`; index row in `contracts/README.md`; `SocAlytics.Platform.Contracts.Tests` |

The workload contracts (`work-notification`, `execution-envelope`, `attempt-report`) do not change: suppression reuses
the existing fenced-attempt rejection, so workloads need no new operation.

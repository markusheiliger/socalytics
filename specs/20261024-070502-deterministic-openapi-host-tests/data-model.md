# Data Model: Deterministic OpenAPI Document Host Tests

The feature changes test support only; it adds no persisted data, table, or contract. The test-support
entities are:

| Entity | Role | Rules |
| --- | --- | --- |
| `StampCollection` | xUnit collection definition named `Stamp`; every class that starts an AppHost joins it | Classes of one collection never run concurrently; the collection does not disable parallelism for other classes |
| `StampFixture` | Collection fixture that builds, starts, and owns one `DistributedApplication` | State: `NotStarted` → `Starting` → `Ready` or `Failed`; a `Failed` fixture rethrows its recorded exception to every test; `Ready` is entered only after `api` was observed healthy (and, if the evidence requires, the document was observed served) |
| Shared stamp options | `--SocAlytics:LocalDatabase:Persistent=false`, `--SocAlytics:ApiHttpsEndpoint=false` | Identical to the options every OpenAPI test passes today; a test needing different options builds its own AppHost |

State transitions: the fixture starts once (`InitializeAsync`), is read-only for tests, and stops once
(`DisposeAsync`). No test mutates the shared stamp.

# Contract: Shared Stamp Test Support

This is a test-support contract of `src/platform/Tests/SocAlytics.Platform.Host.Tests`; it adds no
platform API, and the OpenAPI snapshot at `src/platform/SocAlytics.Platform.Api/openapi/v1.json` is
unchanged.

## Surface

- `StampCollection`: `[CollectionDefinition("Stamp")]` implementing `ICollectionFixture<StampFixture>`.
- `StampFixture` (`IAsyncLifetime`):
  - `InitializeAsync()` builds and starts the AppHost with the shared stamp options, waits for `api`
    healthy under one bounded token (the existing 5-minute limit), and records any exception.
  - `CreateApiClient()` returns an `HttpClient` for the `api` resource's `http` endpoint, or rethrows the
    recorded start failure.
  - `DisposeAsync()` stops and disposes the stamp once.

## Rules

1. Every Host test class that starts an AppHost has `[Collection("Stamp")]`.
2. A test that reads the OpenAPI document or the running stamp uses the fixture and keeps all of its assertions.
3. A test that needs different options, a modified resource, or the start order builds its own AppHost and
   keeps its own limit; it never changes the shared stamp.
4. No fixed delay, retry, skip, or longer limit is added; waits observe a condition under a bounded token.
5. `OpenApiSnapshotTests` keeps `SOCALYTICS_UPDATE_OPENAPI_SNAPSHOT=1` behavior unchanged.
